using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Service d'indexation optimisé avec support du parallélisme et annulation.
/// 
/// <b>Stratégie d'accès SQLite (deux chemins) :</b>
/// <list type="bullet">
///   <item><c>_persistentConnection</c> + <c>_dbLock</c> : opérations CRUD rapides
///         (<see cref="RecordUsage"/>, <see cref="AddOrUpdateItem"/>, <see cref="RemoveItem"/>,
///          <see cref="RemoveItemsByFolder"/>, <see cref="LoadCacheFromDatabase"/>).
///         Synchrone, verrouillé pour éviter les accès concurrents sur la même connexion.</item>
///   <item>Connexions éphémères : opérations bulk longues
///         (<see cref="SaveToDatabaseAsync"/>, purge dans <see cref="RefreshVolatileSourcesAsync"/>).
///         Exécutées en async sur un thread pool. Une connexion séparée évite de bloquer
///         le CRUD pendant les réindexations qui peuvent durer plusieurs secondes.
///         Le mode WAL de SQLite permet la concurrence lecture/écriture entre connexions.</item>
/// </list>
/// </summary>
public sealed partial class IndexingService : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    // Amélioration #5 : OrdinalIgnoreCase pour éviter les doublons sur des paths de casses différentes (Windows)
    private readonly ConcurrentDictionary<string, SearchResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly ILogger _logger;
    private readonly ISettingsProvider _settingsProvider;
    private readonly FolderFingerprintService _fingerprintService;
    private readonly IStoreAppService _storeAppService;
    private readonly IBookmarkService _bookmarkService;
    private readonly IWindowsSettingsProvider _windowsSettingsProvider;
    private readonly IShortcutHelper _shortcutHelper;
    
    // Amélioration #2 : connexion SQLite persistante pour éviter d'ouvrir/fermer à chaque opération
    private readonly SqliteConnection _persistentConnection;
    private readonly object _dbLock = new();
    
    private CancellationTokenSource? _indexingCts;
    private bool _disposed;
    
    public event EventHandler? IndexingStarted;
    public event EventHandler? IndexingCompleted;
    public event EventHandler<int>? IndexingProgress;
    
    public bool IsIndexing { get; private set; }
    public int IndexedItemsCount => _cache.Count;
    
    /// <summary>
    /// Accès en lecture au cache pour le SearchService (Amélioration #3).
    /// </summary>
    public IReadOnlyDictionary<string, SearchResult> CachedItems => _cache;

    public IndexingService(ISettingsProvider settingsProvider,
        FolderFingerprintService fingerprintService, IStoreAppService storeAppService,
        IBookmarkService bookmarkService, IWindowsSettingsProvider windowsSettingsProvider,
        IShortcutHelper shortcutHelper, ILogger? logger = null)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _fingerprintService = fingerprintService ?? throw new ArgumentNullException(nameof(fingerprintService));
        _storeAppService = storeAppService ?? throw new ArgumentNullException(nameof(storeAppService));
        _bookmarkService = bookmarkService ?? throw new ArgumentNullException(nameof(bookmarkService));
        _windowsSettingsProvider = windowsSettingsProvider ?? throw new ArgumentNullException(nameof(windowsSettingsProvider));
        _shortcutHelper = shortcutHelper ?? throw new ArgumentNullException(nameof(shortcutHelper));
        _logger = logger ?? new FileLogger(Constants.AppName, Constants.LogFileName);
        
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Constants.AppName);
        
        Directory.CreateDirectory(appData);
        _dbPath = Path.Combine(appData, Constants.DatabaseFileName);
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";
        
        // Amélioration #2 : connexion persistante en mode WAL pour les opérations fréquentes
        // (RecordUsage, AddOrUpdateItem, RemoveItem, InitializeDatabase)
        _persistentConnection = new SqliteConnection(_connectionString);
        _persistentConnection.Open();
        using (var pragmaCmd = _persistentConnection.CreateCommand())
        {
            pragmaCmd.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout={BusyTimeoutMs};";
            pragmaCmd.ExecuteNonQuery();
        }
        
        InitializeDatabase(_persistentConnection);
        LoadCacheFromDatabase();
        
        _logger.Info($"IndexingService initialisé avec {_cache.Count} éléments en cache");
    }

    /// <summary>
    /// Crée les tables et index si nécessants. Réutilise la connexion persistante
    /// pour éviter une ouverture/fermeture superflue au démarrage.
    /// </summary>
    private static void InitializeDatabase(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Items (
                Path TEXT PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE,
                Description TEXT,
                Type INTEGER NOT NULL,
                LastUsed TEXT,
                UseCount INTEGER DEFAULT 0,
                IndexedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_name ON Items(Name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_usecount ON Items(UseCount DESC);
            CREATE INDEX IF NOT EXISTS idx_type ON Items(Type);
            """;
        cmd.ExecuteNonQuery();
    }
    
    /// <summary>
    /// Délai d'attente (ms) avant qu'une connexion abandonne sur un verrou SQLite.
    /// </summary>
    private const int BusyTimeoutMs = 3000;

    /// <summary>
    /// Applique le busy_timeout sur une connexion fraîchement ouverte. Évite les erreurs
    /// SQLITE_BUSY quand une connexion éphémère (réindexation) et la connexion persistante
    /// (RecordUsage / FileWatcher) tentent d'écrire en même temps en mode WAL : au lieu
    /// d'échouer immédiatement, le second writer patiente jusqu'à BusyTimeoutMs.
    /// </summary>
    private static void ApplyBusyTimeout(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs};";
        cmd.ExecuteNonQuery();
    }

    private void LoadCacheFromDatabase()
    {
        _cache.Clear();
        
        lock (_dbLock)
        {
            using var cmd = _persistentConnection.CreateCommand();
            cmd.CommandText = "SELECT Path, Name, Description, Type, LastUsed, UseCount FROM Items";
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var item = new SearchResult
                {
                    Path = reader.GetString(0),
                    Name = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Type = (ResultType)reader.GetInt32(3),
                    LastUsed = reader.IsDBNull(4)
                        ? DateTime.MinValue
                        : DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    UseCount = reader.GetInt32(5)
                };
                _cache[item.Path] = item;
            }
        }
    }

    public async Task StartIndexingAsync(CancellationToken cancellationToken = default)
    {
        if (IsIndexing) return;
        
        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            if (IsIndexing) return;
            
            IsIndexing = true;
            _indexingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IndexingStarted?.Invoke(this, EventArgs.Empty);
            
            var settings = _settingsProvider.Current;
            _logger.Info("Démarrage de l'indexation...");
            
            var items = new ConcurrentBag<SearchResult>();
            var token = _indexingCts.Token;
            
            // Indexer les apps Store en parallèle
            var storeTask = Task.Run(() =>
            {
                var storeApps = _storeAppService.GetAllApps();
                foreach (var app in storeApps)
                    items.Add(app);
                _logger.Info($"Apps Store: {storeApps.Count} trouvées");
            }, token);
            
            // Indexer les favoris des navigateurs en parallèle
            var bookmarksTask = Task.Run(() =>
            {
                if (settings.Search.IndexBrowserBookmarks)
                {
                    var bookmarks = _bookmarkService.GetAllBookmarks();
                    foreach (var bookmark in bookmarks)
                        items.Add(bookmark);
                    _logger.Info($"Favoris navigateurs: {bookmarks.Count} trouvés");
                }
            }, token);
            
            // Ajouter les pages de paramètres Windows (Amélioration #3 : extrait dans WindowsSettingsProvider)
            var windowsSettings = _windowsSettingsProvider.GetItems();
            foreach (var ws in windowsSettings)
                items.Add(ws);
            _logger.Info($"Paramètres Windows: {windowsSettings.Count} ajoutés");
            
            // Indexer les dossiers en parallèle
            var folderTasks = settings.Search.IndexedFolders
                .Where(Directory.Exists)
                .Select(folder => Task.Run(() => IndexFolder(folder, items, settings, token), token))
                .ToArray();
            
            await Task.WhenAll([storeTask, bookmarksTask, ..folderTasks]);

            // Ajouter les scripts personnalisés
            foreach (var script in settings.Search.Scripts)
            {
                items.Add(new SearchResult
                {
                    Name = script.Name,
                    Path = script.Command,
                    Description = $"Script: {script.Keyword}",
                    Type = ResultType.Script
                });
            }
            
            // Dédupliquer par (nom + catégorie de type) pour éviter de masquer des fichiers différents portant le même nom.
            // Application et StoreApp sont regroupés dans la même catégorie car un même programme
            // peut apparaître via shell:AppsFolder (StoreApp) ET via un raccourci .lnk (Application).
            // Les items de catégories différentes avec le même nom sont conservés (ex: "Config" fichier + "Config" dossier).
            var deduplicated = items
                .GroupBy(i => (Name: i.Name.ToLowerInvariant(), TypeCategory: DeduplicationHelper.GetCategory(i.Type)))
                .Select(g => g.OrderByDescending(i => i.Type == ResultType.StoreApp ? 1 : 0)
                              .ThenByDescending(i => i.UseCount)
                              .First())
                .ToList();
            
            _logger.Info($"Total: {deduplicated.Count} éléments (après déduplication)");
            
            await SaveToDatabaseAsync(deduplicated, token);
            LoadCacheFromDatabase();
            
            // Sauvegarder les fingerprints pour le prochain démarrage intelligent
            SaveCurrentFingerprints(settings);
        }
        finally
        {
            IsIndexing = false;
            _indexingCts?.Dispose();
            _indexingCts = null;
            _indexLock.Release();
            IndexingCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void IndexFolder(string folderPath, ConcurrentBag<SearchResult> items, AppSettings settings, CancellationToken token)
    {
        var count = 0;
        IndexFolderRecursive(folderPath, items, settings, ref count, 0, token);
        _logger.Info($"Dossier '{Path.GetFileName(folderPath)}': {count} éléments");
    }
    
    private void IndexFolderRecursive(string folderPath, ConcurrentBag<SearchResult> items,
        AppSettings settings, ref int count, int depth, CancellationToken token)
    {
        if (depth > settings.Search.SearchDepth || token.IsCancellationRequested) return;
        
        try
        {
            var dirInfo = new DirectoryInfo(folderPath);
            if (!settings.Search.IndexHiddenFolders && (dirInfo.Attributes & FileAttributes.Hidden) != 0)
                return;
            
            // Indexer les fichiers
            foreach (var file in dirInfo.EnumerateFiles())
            {
                if (token.IsCancellationRequested) return;
                
                var ext = file.Extension.ToLowerInvariant();
                if (!settings.Search.FileExtensions.Contains(ext)) continue;
                if (!settings.Search.IndexHiddenFolders && (file.Attributes & FileAttributes.Hidden) != 0) continue;
                
                var result = CreateSearchResult(file.FullName);
                if (result != null)
                {
                    items.Add(result);
                    Interlocked.Increment(ref count);
                }
            }
            
            // Parcourir les sous-dossiers
            foreach (var subDir in dirInfo.EnumerateDirectories())
            {
                if (token.IsCancellationRequested) return;
                IndexFolderRecursive(subDir.FullName, items, settings, ref count, depth + 1, token);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (Exception ex)
        {
            _logger.Warning($"Erreur indexation '{folderPath}': {ex.Message}");
        }
    }

    private SearchResult? CreateSearchResult(string filePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var description = filePath;
            var targetPath = filePath;
            
            if (ext == ".lnk")
            {
                var info = _shortcutHelper.ResolveShortcut(filePath);
                if (info != null)
                {
                    targetPath = info.TargetPath;
                    description = string.IsNullOrEmpty(info.Description) ? targetPath : info.Description;
                }
            }

            var type = ext switch
            {
                ".exe" or ".lnk" or ".msi" => ResultType.Application,
                ".bat" or ".cmd" or ".ps1" => ResultType.Script,
                _ => ResultType.File
            };
            
            if (Directory.Exists(targetPath)) 
                type = ResultType.Folder;
            
            return new SearchResult 
            { 
                Name = name, 
                Path = filePath, 
                Description = description, 
                Type = type 
            };
        }
        catch (Exception ex)
        {
            // Amélioration #6 : logger les erreurs au lieu de les avaler silencieusement
            _logger.Warning($"Erreur CreateSearchResult '{filePath}': {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Persiste les items indexés en base.
    /// Utilise une connexion éphémère pour ne pas bloquer le CRUD persistant
    /// pendant cette longue transaction (potentiellement des milliers d'INSERTs).
    /// </summary>
    private async Task SaveToDatabaseAsync(List<SearchResult> items, CancellationToken token)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(token);
        ApplyBusyTimeout(conn);

        await using var transaction = conn.BeginTransaction();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO Items (Path, Name, Description, Type, UseCount, IndexedAt)
                VALUES (@path, @name, @desc, @type, 
                    COALESCE((SELECT UseCount FROM Items WHERE Path = @path), 0), 
                    @indexed)
                """;
            
            var pathParam = cmd.Parameters.Add("@path", SqliteType.Text);
            var nameParam = cmd.Parameters.Add("@name", SqliteType.Text);
            var descParam = cmd.Parameters.Add("@desc", SqliteType.Text);
            var typeParam = cmd.Parameters.Add("@type", SqliteType.Integer);
            var indexedParam = cmd.Parameters.Add("@indexed", SqliteType.Text);
            
            // Pré-compiler la requête pour éviter le re-parse à chaque itération
            cmd.Prepare();
            
            var now = DateTime.UtcNow.ToString("o");
            var progress = 0;
            
            foreach (var item in items)
            {
                if (token.IsCancellationRequested) break;
                
                pathParam.Value = item.Path;
                nameParam.Value = item.Name;
                descParam.Value = item.Description;
                typeParam.Value = (int)item.Type;
                indexedParam.Value = now;
                
                await cmd.ExecuteNonQueryAsync(token);
                
                if (++progress % Constants.IndexingBatchSize == 0)
                    IndexingProgress?.Invoke(this, progress);
            }
            
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    public async Task ReindexAsync(CancellationToken cancellationToken = default)
    {
        CancelIndexing();

        // L'annulation est COOPÉRATIVE : CancelIndexing() ne fait que lever le drapeau.
        // Une passe en cours peut encore être dans SaveToDatabaseAsync en train de committer
        // sa transaction. Purger la table sans attendre laisserait survivre les lignes
        // écrites après le DELETE — l'index se retrouve avec des entrées fantômes
        // (fichiers supprimés toujours proposés) ou incomplet.
        // Prendre _indexLock garantit que la passe précédente est réellement terminée.
        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            // Vider la table via la connexion persistante (opération rapide)
            lock (_dbLock)
            {
                using var cmd = _persistentConnection.CreateCommand();
                cmd.CommandText = "DELETE FROM Items";
                cmd.ExecuteNonQuery();
            }

            _cache.Clear();

            // Purger le cache de distances fuzzy pour éviter l'accumulation d'entrées périmées
            SearchAlgorithms.ClearCache();
        }
        finally
        {
            // Libérer avant StartIndexingAsync, qui reprend le verrou lui-même.
            _indexLock.Release();
        }

        await StartIndexingAsync(cancellationToken);
    }
    
    public void CancelIndexing() => _indexingCts?.Cancel();

    // Search(), CalculateScore() et TryCalculate() ont été déplacés
    // vers SearchService et CalculatorService (Amélioration #3 et #7).
    // Utiliser SearchService.Search() à la place.
    
    // Déduplication centralisée dans DeduplicationHelper.
    
    public void RecordUsage(SearchResult item)
    {
        try
        {
            // Amélioration #2 : utilise la connexion persistante (pas d'ouverture/fermeture)
            lock (_dbLock)
            {
                using var cmd = _persistentConnection.CreateCommand();
                cmd.CommandText = "UPDATE Items SET UseCount = UseCount + 1, LastUsed = @now WHERE Path = @path";
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("@path", item.Path);
                cmd.ExecuteNonQuery();
            }
            
            if (_cache.TryGetValue(item.Path, out var cached))
            {
                cached.UseCount++;
                cached.LastUsed = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Erreur RecordUsage: {ex.Message}");
        }
    }
    
    #region Incremental Indexing
    
    /// <summary>
    /// Ajoute ou met à jour un fichier dans l'index de manière incrémentale.
    /// </summary>
    public void AddOrUpdateItem(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        
        try
        {
            var result = CreateSearchResult(filePath);
            if (result == null) return;
            
            // Amélioration #2 : connexion persistante
            lock (_dbLock)
            {
                using var cmd = _persistentConnection.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO Items (Path, Name, Description, Type, UseCount, IndexedAt)
                    VALUES (@path, @name, @desc, @type, 
                        COALESCE((SELECT UseCount FROM Items WHERE Path = @path), 0), 
                        @indexed)
                    """;
                cmd.Parameters.AddWithValue("@path", result.Path);
                cmd.Parameters.AddWithValue("@name", result.Name);
                cmd.Parameters.AddWithValue("@desc", result.Description);
                cmd.Parameters.AddWithValue("@type", (int)result.Type);
                cmd.Parameters.AddWithValue("@indexed", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            
            _cache[result.Path] = result;
            _logger.Info($"[Incremental] Ajouté/MàJ: {result.Name}");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Erreur AddOrUpdateItem '{filePath}': {ex.Message}");
        }
    }
    
    /// <summary>
    /// Supprime un fichier de l'index de manière incrémentale.
    /// </summary>
    public void RemoveItem(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        
        try
        {
            int affected;
            // Amélioration #2 : connexion persistante
            lock (_dbLock)
            {
                using var cmd = _persistentConnection.CreateCommand();
                cmd.CommandText = "DELETE FROM Items WHERE Path = @path";
                cmd.Parameters.AddWithValue("@path", filePath);
                affected = cmd.ExecuteNonQuery();
            }
            
            _cache.TryRemove(filePath, out _);
            IconCacheService.Invalidate(filePath);
            
            if (affected > 0)
                _logger.Info($"[Incremental] Supprimé: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Erreur RemoveItem '{filePath}': {ex.Message}");
        }
    }
    
    /// <summary>
    /// Traite une liste de changements de fichiers de manière incrémentale.
    /// </summary>
    public void ProcessFileChanges(IEnumerable<FileChangeEvent> changes)
    {
        var changeList = changes.ToList();
        if (changeList.Count == 0) return;
        
        _logger.Info($"[Incremental] Traitement de {changeList.Count} changements...");
        
        foreach (var change in changeList)
        {
            switch (change.Type)
            {
                case FileChangeType.Created:
                    AddOrUpdateItem(change.Path);
                    break;
                    
                case FileChangeType.Deleted:
                    RemoveItem(change.Path);
                    // La suppression d'un dossier ne génère AUCUN événement pour son
                    // contenu (le shell déplace l'arborescence d'un bloc) : purger
                    // aussi les entrées enfants pour ne pas laisser d'items fantômes.
                    // Heuristique : un chemin sans extension est un dossier potentiel
                    // (impossible de vérifier sur disque, il n'existe plus).
                    if (!Path.HasExtension(change.Path))
                        RemoveItemsByFolder(change.Path);
                    break;
                    
                case FileChangeType.Modified:
                    AddOrUpdateItem(change.Path);
                    break;
            }
        }
        
        _logger.Info($"[Incremental] Terminé. Cache: {_cache.Count} éléments");
    }
    
    #endregion
    
    #region Smart Persistent Index
    
    /// <summary>
    /// Démarrage intelligent : utilise le cache SQLite existant + fingerprints
    /// pour ne réindexer que les dossiers modifiés.
    /// Premier lancement = indexation complète. Lancements suivants = quasi-instantané.
    /// </summary>
    public async Task SmartStartIndexingAsync(CancellationToken cancellationToken = default)
    {
        // Si le cache est vide (premier lancement ou DB supprimée), indexation complète
        if (_cache.Count == 0)
        {
            _logger.Info("[SmartIndex] Cache vide — indexation complète...");
            await StartIndexingAsync(cancellationToken);
            return;
        }
        
        _logger.Info($"[SmartIndex] Cache chargé avec {_cache.Count} éléments. Vérification des changements...");
        
        var settings = _settingsProvider.Current;
        var comparison = _fingerprintService.CompareWithStored(
            settings.Search.IndexedFolders,
            settings.Search.FileExtensions,
            settings.Search.SearchDepth,
            settings.Search.IndexHiddenFolders);
        
        if (!comparison.HasChanges)
        {
            _logger.Info("[SmartIndex] Aucun changement de dossiers — rafraîchissement des sources volatiles uniquement...");
            
            // Toujours rafraîchir les sources volatiles (Store apps, bookmarks, paramètres Windows)
            // même si les dossiers n'ont pas changé, pour garantir qu'elles sont à jour.
            await RefreshVolatileSourcesAsync(settings, cancellationToken);
            
            IndexingCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }
        
        _logger.Info($"[SmartIndex] Changements: {comparison.NewFolders.Count} nouveaux, " +
                     $"{comparison.ModifiedFolders.Count} modifiés, {comparison.DeletedFolders.Count} supprimés");
        
        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            IsIndexing = true;
            _indexingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IndexingStarted?.Invoke(this, EventArgs.Empty);
            var token = _indexingCts.Token;
            
            // 1. Supprimer les items des dossiers supprimés
            foreach (var deletedFolder in comparison.DeletedFolders)
            {
                RemoveItemsByFolder(deletedFolder);
                _fingerprintService.RemoveFingerprint(deletedFolder);
            }
            
            // 2. Réindexer uniquement les dossiers nouveaux ou modifiés
            var foldersToIndex = comparison.NewFolders
                .Concat(comparison.ModifiedFolders)
                .Where(Directory.Exists)
                .ToList();
            
            if (foldersToIndex.Count > 0)
            {
                var items = new ConcurrentBag<SearchResult>();
                
                // Pour les dossiers modifiés, supprimer les anciens items d'abord
                foreach (var modifiedFolder in comparison.ModifiedFolders)
                {
                    RemoveItemsByFolder(modifiedFolder);
                }
                
                // Réindexer en parallèle
                var folderTasks = foldersToIndex
                    .Select(folder => Task.Run(() => IndexFolder(folder, items, settings, token), token))
                    .ToArray();
                
                await Task.WhenAll(folderTasks);
                
                // Sauvegarder les nouveaux items
                var newItems = items.ToList();
                if (newItems.Count > 0)
                {
                    await SaveToDatabaseAsync(newItems, token);
                    LoadCacheFromDatabase();
                }
                
                _logger.Info($"[SmartIndex] {newItems.Count} éléments réindexés");
            }
            
            // 3. Réindexer les bookmarks et Store apps (rapide, toujours frais)
            await RefreshVolatileSourcesAsync(settings, _indexingCts.Token);
            
            // 4. Sauvegarder les nouveaux fingerprints
            SaveCurrentFingerprints(settings);
            
            _logger.Info($"[SmartIndex] Terminé. Cache: {_cache.Count} éléments");
        }
        finally
        {
            IsIndexing = false;
            _indexingCts?.Dispose();
            _indexingCts = null;
            _indexLock.Release();
            IndexingCompleted?.Invoke(this, EventArgs.Empty);
        }
    }
    
    /// <summary>
    /// Supprime tous les items de l'index dont le chemin commence par le dossier spécifié.
    /// </summary>
    private void RemoveItemsByFolder(string folderPath)
    {
        var normalizedFolder = folderPath.TrimEnd('\\', '/');
        // Préfixe avec séparateur final : un dossier ne « contient » que les chemins
        // qui commencent par "<dossier>\". Sans le séparateur, supprimer "C:\Data"
        // évincerait aussi les items du dossier frère "C:\Database".
        var childPrefix = normalizedFolder + "\\";
        var keysToRemove = _cache.Keys
            .Where(k => k.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
        }

        if (keysToRemove.Count > 0)
        {
            try
            {
                // Échapper les jokers LIKE (% et _) légaux dans un nom de dossier Windows.
                // '|' est interdit dans un chemin Windows → caractère d'échappement sûr.
                var likePrefix = childPrefix
                    .Replace("|", "||")
                    .Replace("%", "|%")
                    .Replace("_", "|_");

                // Amélioration #2 : connexion persistante
                lock (_dbLock)
                {
                    using var cmd = _persistentConnection.CreateCommand();
                    cmd.CommandText = "DELETE FROM Items WHERE Path = @exact OR Path LIKE @prefix ESCAPE '|'";
                    cmd.Parameters.AddWithValue("@exact", normalizedFolder);
                    cmd.Parameters.AddWithValue("@prefix", likePrefix + "%");
                    var deleted = cmd.ExecuteNonQuery();
                    _logger.Info($"[SmartIndex] Supprimé {deleted} items de '{Path.GetFileName(normalizedFolder)}'");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Erreur RemoveItemsByFolder: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Réindexe les sources volatiles (Store apps, bookmarks, paramètres Windows)
    /// qui doivent toujours être présentes dans l'index.
    /// </summary>
    private async Task RefreshVolatileSourcesAsync(AppSettings settings, CancellationToken token)
    {
        var items = new ConcurrentBag<SearchResult>();
        
        var storeTask = Task.Run(() =>
        {
            var storeApps = _storeAppService.GetAllApps();
            foreach (var app in storeApps) items.Add(app);
        }, token);
        
        var bookmarksTask = Task.Run(() =>
        {
            if (settings.Search.IndexBrowserBookmarks)
            {
                var bookmarks = _bookmarkService.GetAllBookmarks();
                foreach (var bm in bookmarks) items.Add(bm);
            }
        }, token);
        
        await Task.WhenAll(storeTask, bookmarksTask);
        
        // Ajouter les pages de paramètres Windows (toujours présentes)
        var windowsSettings = _windowsSettingsProvider.GetItems();
        foreach (var ws in windowsSettings)
            items.Add(ws);
        
        // Supprimer les anciens Store apps, bookmarks et SystemControl du cache ET de la DB
        var volatileTypes = new[] { ResultType.StoreApp, ResultType.Bookmark, ResultType.SystemControl };
        var keysToRemove = _cache
            .Where(kv => volatileTypes.Contains(kv.Value.Type))
            .Select(kv => kv.Key)
            .ToList();
        
        foreach (var key in keysToRemove)
            _cache.TryRemove(key, out _);
        
        // Purger les entrées volatiles PÉRIMÉES de la base de données.
        // On ne supprime que celles dont le Path n'existe plus dans le nouveau set,
        // pour éviter que des entrées fantômes (ex: app réinstallée avec un nouveau AppUserModelId)
        // ne réapparaissent au prochain LoadCacheFromDatabase() tout en préservant le UseCount
        // des entrées toujours valides.
        try
        {
            var freshPaths = new HashSet<string>(items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            var stalePaths = keysToRemove.Where(k => !freshPaths.Contains(k)).ToList();
            
            if (stalePaths.Count > 0)
            {
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync(token);
                ApplyBusyTimeout(conn);

                // SQLite ne supporte pas les paramètres de tableau, on construit la requête
                await using var deleteCmd = conn.CreateCommand();
                var paramNames = new List<string>(stalePaths.Count);
                for (var i = 0; i < stalePaths.Count; i++)
                {
                    var paramName = $"@p{i}";
                    paramNames.Add(paramName);
                    deleteCmd.Parameters.AddWithValue(paramName, stalePaths[i]);
                }
                deleteCmd.CommandText = $"DELETE FROM Items WHERE Path IN ({string.Join(", ", paramNames)})";
                var purged = await deleteCmd.ExecuteNonQueryAsync(token);
                _logger.Info($"[Volatile] Purgé {purged} entrées volatiles périmées de la DB");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Erreur purge volatiles: {ex.Message}");
        }
        
        // Sauvegarder les items frais
        var newItems = items.ToList();
        if (newItems.Count > 0)
        {
            await SaveToDatabaseAsync(newItems, token);
            LoadCacheFromDatabase();
        }
    }
    
    /// <summary>
    /// Sauvegarde les fingerprints pour tous les dossiers indexés actuels.
    /// </summary>
    private void SaveCurrentFingerprints(AppSettings settings)
    {
        var fingerprints = settings.Search.IndexedFolders
            .Where(Directory.Exists)
            .Select(folder => _fingerprintService.ComputeFingerprint(
                folder, settings.Search.FileExtensions, settings.Search.SearchDepth, settings.Search.IndexHiddenFolders))
            .ToList();
        
        _fingerprintService.SaveFingerprints(fingerprints);
    }
    
    #endregion
    
    // Région Windows Settings Items supprimée — déplacée dans WindowsSettingsProvider (Amélioration #3).
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        CancelIndexing();
        _indexLock.Dispose();
        _cache.Clear();
        
        // Amélioration #2 : fermer la connexion persistante
        try { _persistentConnection.Close(); _persistentConnection.Dispose(); }
        catch { /* Ignore en shutdown */ }
        
        // Note: _fingerprintService est disposé par le conteneur DI
        GC.SuppressFinalize(this);
    }
}

