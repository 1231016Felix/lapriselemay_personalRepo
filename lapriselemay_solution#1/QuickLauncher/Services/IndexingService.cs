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
        IShortcutHelper shortcutHelper, ILogger? logger = null, string? databasePath = null)
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
        _dbPath = databasePath ?? Path.Combine(appData, Constants.DatabaseFileName);
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
    /// <summary>Schéma de la table d'index (exposé pour les tests).</summary>
    internal const string SchemaSql = """
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

    /// <summary>
    /// Insère ou met à jour les métadonnées d'un élément SANS toucher à UseCount ni LastUsed.
    /// L'ancien « INSERT OR REPLACE » supprimait puis recréait la ligne : LastUsed repartait
    /// à NULL à chaque réindexation et le classement personnalisé se perdait.
    /// </summary>
    internal const string UpsertItemSql = """
        INSERT INTO Items (Path, Name, Description, Type, UseCount, IndexedAt)
        VALUES (@path, @name, @desc, @type, 0, @indexed)
        ON CONFLICT(Path) DO UPDATE SET
            Name = excluded.Name,
            Description = excluded.Description,
            Type = excluded.Type,
            IndexedAt = excluded.IndexedAt
        """;

    /// <summary>
    /// Horodatage d'une passe d'indexation (ISO 8601 UTC, triable lexicographiquement).
    /// Les lignes non revues pendant une passe complète gardent un IndexedAt plus ancien
    /// et sont balayées à la fin : c'est ce qui remplace les DELETE massifs.
    /// </summary>
    private static string NewIndexStamp() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static void InitializeDatabase(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
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
        var fresh = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);

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
                fresh[item.Path] = item;
            }
        }

        // Remplacer le contenu SANS fenêtre vide : une recherche concurrente voit l'ancien
        // ou le nouvel élément, jamais un cache momentanément vidé (l'ancien Clear() le faisait).
        foreach (var key in _cache.Keys)
        {
            if (!fresh.ContainsKey(key))
                _cache.TryRemove(key, out _);
        }
        foreach (var (key, item) in fresh)
            _cache[key] = item;
    }

    public async Task StartIndexingAsync(CancellationToken cancellationToken = default)
    {
        if (IsIndexing) return;

        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsIndexing) return;

            IsIndexing = true;
            _indexingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IndexingStarted?.Invoke(this, EventArgs.Empty);

            var settings = _settingsProvider.Current;
            _logger.Info("Démarrage de l'indexation...");

            var runStamp = NewIndexStamp();
            var items = new ConcurrentBag<SearchResult>();
            var unreachableRoots = new ConcurrentBag<string>();
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

            // Indexer les dossiers en parallèle. Une racine inaccessible (accès refusé, erreur
            // d'E/S) est notée : ses entrées existantes ne seront pas balayées.
            var folderTasks = settings.Search.IndexedFolders
                .Select(folder => Task.Run(() =>
                {
                    if (!IndexFolder(folder, items, settings, token))
                        unreachableRoots.Add(folder);
                }, token))
                .ToArray();

            await Task.WhenAll([storeTask, bookmarksTask, ..folderTasks]).ConfigureAwait(false);

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

            // Déduplication par IDENTITÉ (cf. DeduplicationHelper.GetIdentityKey) : les entrées
            // d'application sont fusionnées par nom (raccourci + AppsFolder), mais deux fichiers
            // homonymes de dossiers différents restent deux éléments.
            var deduplicated = items
                .GroupBy(i => DeduplicationHelper.GetIdentityKey(i))
                .Select(g => g.OrderByDescending(i => i.Type == ResultType.StoreApp ? 1 : 0)
                              .ThenByDescending(i => i.UseCount)
                              .First())
                .ToList();

            _logger.Info($"Total: {deduplicated.Count} éléments (après déduplication)");

            // UPSERT (usage conservé) puis balayage de ce qui n'a pas été revu. Le balayage
            // n'a lieu qu'après une passe COMPLÈTE, et jamais sous une racine inaccessible.
            await SaveToDatabaseAsync(deduplicated, runStamp, token).ConfigureAwait(false);

            var allRoots = settings.Search.IndexedFolders.ToList();
            var unreachable = unreachableRoots.Select(IndexRules.NormalizeDirectory)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await SweepAsync(runStamp,
                path => IndexRules.FindRoot(path, allRoots) is not { } root || !unreachable.Contains(root),
                token).ConfigureAwait(false);

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

    /// <summary>
    /// Indexe une racine. Une racine qui n'existe plus est considérée comme vide (ses entrées
    /// seront balayées) ; une racine existante mais illisible renvoie <c>false</c> pour que
    /// ses entrées et leur usage soient conservés.
    /// </summary>
    private bool IndexFolder(string folderPath, ConcurrentBag<SearchResult> items, AppSettings settings, CancellationToken token)
    {
        if (!Directory.Exists(folderPath))
        {
            _logger.Info($"Dossier '{folderPath}' introuvable : ignoré");
            return true;
        }

        try
        {
            using var probe = Directory.EnumerateFileSystemEntries(folderPath).GetEnumerator();
            probe.MoveNext();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.Warning($"Dossier '{folderPath}' inaccessible, ses entrées sont conservées : {ex.Message}");
            return false;
        }

        var count = 0;
        IndexFolderRecursive(folderPath, items, settings, ref count, 0, token);
        _logger.Info($"Dossier '{Path.GetFileName(folderPath)}': {count} éléments");
        return true;
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
    private async Task SaveToDatabaseAsync(List<SearchResult> items, string indexStamp, CancellationToken token)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(token).ConfigureAwait(false);
        ApplyBusyTimeout(conn);

        await using var transaction = conn.BeginTransaction();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = UpsertItemSql;

            var pathParam = cmd.Parameters.Add("@path", SqliteType.Text);
            var nameParam = cmd.Parameters.Add("@name", SqliteType.Text);
            var descParam = cmd.Parameters.Add("@desc", SqliteType.Text);
            var typeParam = cmd.Parameters.Add("@type", SqliteType.Integer);
            var indexedParam = cmd.Parameters.Add("@indexed", SqliteType.Text);

            // Pré-compiler la requête pour éviter le re-parse à chaque itération
            cmd.Prepare();

            var progress = 0;

            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();

                pathParam.Value = item.Path;
                nameParam.Value = item.Name;
                descParam.Value = item.Description ?? string.Empty;
                typeParam.Value = (int)item.Type;
                indexedParam.Value = indexStamp;

                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                if (++progress % Constants.IndexingBatchSize == 0)
                    IndexingProgress?.Invoke(this, progress);
            }

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Supprime les lignes qu'une passe n'a pas revues (IndexedAt antérieur à
    /// <paramref name="runStamp"/>) et qui sont dans la portée de cette passe.
    /// Ne fait rien si la passe a été annulée : tout ce qui n'a pas encore été revu
    /// passerait sinon pour disparu.
    /// </summary>
    private async Task SweepAsync(string runStamp, Func<string, bool> inScope, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(token).ConfigureAwait(false);
        ApplyBusyTimeout(conn);

        var stale = new List<string>();
        await using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT Path FROM Items WHERE IndexedAt < @stamp";
            select.Parameters.AddWithValue("@stamp", runStamp);
            await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                if (inScope(path))
                    stale.Add(path);
            }
        }

        if (stale.Count == 0 || token.IsCancellationRequested) return;

        await using var transaction = conn.BeginTransaction();
        await using (var delete = conn.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Items WHERE Path = @path";
            var pathParam = delete.Parameters.Add("@path", SqliteType.Text);
            delete.Prepare();
            foreach (var path in stale)
            {
                pathParam.Value = path;
                await delete.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (var path in stale)
            _cache.TryRemove(path, out _);

        _logger.Info($"[Index] {stale.Count} entrée(s) disparue(s) retirée(s)");
    }

    /// <summary>
    /// Réindexation complète à la demande (menu, Ctrl+R, import de favoris, réindexation auto).
    /// Ne vide plus la table : la passe complète fait un UPSERT puis balaie ce qui a disparu,
    /// si bien que UseCount et LastUsed survivent aux réindexations.
    /// </summary>
    public async Task ReindexAsync(CancellationToken cancellationToken = default)
    {
        CancelIndexing();

        // L'annulation est COOPÉRATIVE : CancelIndexing() ne fait que lever le drapeau.
        // Attendre _indexLock garantit que la passe précédente est réellement terminée
        // avant d'en démarrer une nouvelle.
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Purger le cache de distances fuzzy pour éviter l'accumulation d'entrées périmées
            SearchAlgorithms.ClearCache();
        }
        finally
        {
            // Libérer avant StartIndexingAsync, qui reprend le verrou lui-même.
            _indexLock.Release();
        }

        await StartIndexingAsync(cancellationToken).ConfigureAwait(false);
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

            // UPSERT : conserve UseCount et LastUsed (base ET cache)
            UpsertItems([result]);
            _logger.Info($"[Incremental] Ajouté/MàJ: {result.Name}");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Erreur AddOrUpdateItem '{filePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// UPSERT synchrone sur la connexion persistante, puis mise à jour du cache en conservant
    /// l'usage déjà connu des éléments existants.
    /// </summary>
    private void UpsertItems(IReadOnlyCollection<SearchResult> items, string? indexStamp = null)
    {
        if (items.Count == 0) return;
        var stamp = indexStamp ?? NewIndexStamp();

        lock (_dbLock)
        {
            using var transaction = _persistentConnection.BeginTransaction();
            using var cmd = _persistentConnection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = UpsertItemSql;
            var pathParam = cmd.Parameters.Add("@path", SqliteType.Text);
            var nameParam = cmd.Parameters.Add("@name", SqliteType.Text);
            var descParam = cmd.Parameters.Add("@desc", SqliteType.Text);
            var typeParam = cmd.Parameters.Add("@type", SqliteType.Integer);
            var indexedParam = cmd.Parameters.Add("@indexed", SqliteType.Text);

            foreach (var item in items)
            {
                pathParam.Value = item.Path;
                nameParam.Value = item.Name;
                descParam.Value = item.Description ?? string.Empty;
                typeParam.Value = (int)item.Type;
                indexedParam.Value = stamp;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        foreach (var item in items)
        {
            if (_cache.TryGetValue(item.Path, out var existing))
            {
                item.UseCount = existing.UseCount;
                item.LastUsed = existing.LastUsed;
            }
            _cache[item.Path] = item;
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
        var settings = _settingsProvider.Current;

        // 1. Suppressions d'abord : un renommage arrive en Deleted(ancien) + Created(nouveau).
        var deleted = changeList.Where(c => c.Type == FileChangeType.Deleted).Select(c => c.Path).ToList();
        if (deleted.Count > 0)
            RemovePaths(deleted);

        // 2. Créations, arrivées de renommage/déplacement, modifications — avec les MÊMES règles
        //    que le scan complet (IndexRules) : extensions, profondeur, dossiers cachés.
        foreach (var change in changeList.Where(c => c.Type is FileChangeType.Created or FileChangeType.Modified))
        {
            try
            {
                if (Directory.Exists(change.Path))
                {
                    // Dossier créé, copié, renommé ou déplacé : il peut déjà contenir des fichiers
                    IndexSubtree(change.Path, settings);
                }
                else if (IndexRules.IsEligibleFile(change.Path, settings.Search.IndexedFolders,
                             settings.Search.FileExtensions, settings.Search.SearchDepth,
                             settings.Search.IndexHiddenFolders, IndexRules.TryGetAttributes))
                {
                    AddOrUpdateItem(change.Path);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[Incremental] Erreur sur '{change.Path}': {ex.Message}");
            }
        }

        // 3. Racines dont le watcher a perdu des événements (débordement du tampon)
        foreach (var change in changeList.Where(c => c.Type == FileChangeType.Rescan))
            ReconcileRoot(change.Path, settings);

        _logger.Info($"[Incremental] Terminé. Cache: {_cache.Count} éléments");
    }

    /// <summary>
    /// Retire de l'index les chemins supprimés. Un chemin qui n'est pas un élément connu est
    /// traité comme un dossier potentiel : tout ce qui est indexé dessous est retiré. On ne se
    /// fie plus à l'absence d'extension (« Project.v2 » est un nom de dossier valide), et le
    /// lot entier ne coûte qu'une passe sur le cache, quel que soit le nombre de suppressions.
    /// </summary>
    private void RemovePaths(IReadOnlyCollection<string> paths)
    {
        var toRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (_cache.ContainsKey(path))
                toRemove.Add(path);
            else
                folders.Add(IndexRules.NormalizeDirectory(path));
        }

        if (folders.Count > 0)
        {
            foreach (var key in _cache.Keys)
            {
                for (var parent = Path.GetDirectoryName(key); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                {
                    if (folders.Contains(parent))
                    {
                        toRemove.Add(key);
                        break;
                    }
                }
            }
        }

        if (toRemove.Count == 0) return;

        try
        {
            lock (_dbLock)
            {
                using var transaction = _persistentConnection.BeginTransaction();
                using var cmd = _persistentConnection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM Items WHERE Path = @path";
                var pathParam = cmd.Parameters.Add("@path", SqliteType.Text);
                foreach (var path in toRemove)
                {
                    pathParam.Value = path;
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Incremental] Erreur de suppression: {ex.Message}");
        }

        foreach (var path in toRemove)
        {
            _cache.TryRemove(path, out _);
            IconCacheService.Invalidate(path);
        }

        _logger.Info($"[Incremental] Supprimé {toRemove.Count} élément(s)");
    }

    /// <summary>
    /// Indexe le contenu d'un dossier apparu dans une racine (création, copie, renommage ou
    /// déplacement d'un dossier déjà peuplé), dans les limites du scan complet.
    /// </summary>
    private void IndexSubtree(string directory, AppSettings settings)
    {
        var roots = settings.Search.IndexedFolders;
        var root = IndexRules.FindRoot(directory, roots);
        if (root == null) return;

        if (!IndexRules.IsEligibleDirectory(directory, roots, settings.Search.SearchDepth,
                settings.Search.IndexHiddenFolders, IndexRules.TryGetAttributes))
            return;

        var items = new ConcurrentBag<SearchResult>();
        var count = 0;
        IndexFolderRecursive(directory, items, settings, ref count,
            IndexRules.DirectoryDepth(root, directory), CancellationToken.None);

        if (items.IsEmpty) return;

        UpsertItems(items.ToList());
        _logger.Info($"[Incremental] Sous-arbre '{Path.GetFileName(directory)}': {items.Count} éléments");
    }

    /// <summary>
    /// Réconcilie une racine après une perte d'événements du watcher : scan de la racine,
    /// UPSERT, puis balayage de ce qui n'existe plus, sans toucher aux compteurs d'usage.
    /// </summary>
    private void ReconcileRoot(string root, AppSettings settings)
    {
        // Une indexation complète en cours couvre déjà ce dossier
        if (!_indexLock.Wait(0))
        {
            _logger.Info($"[Incremental] Réconciliation de '{root}' reportée : indexation en cours");
            return;
        }

        try
        {
            var items = new ConcurrentBag<SearchResult>();
            if (!IndexFolder(root, items, settings, CancellationToken.None))
                return; // racine illisible : ne rien balayer

            var stamp = NewIndexStamp();
            UpsertItems(items.ToList(), stamp);

            var allRoots = settings.Search.IndexedFolders.ToList();
            var normalizedRoot = IndexRules.NormalizeDirectory(root);
            SweepAsync(stamp,
                    path => string.Equals(IndexRules.FindRoot(path, allRoots), normalizedRoot, StringComparison.OrdinalIgnoreCase),
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            LoadCacheFromDatabase();
            _logger.Info($"[Incremental] Racine '{root}' réconciliée ({items.Count} éléments)");
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Incremental] Réconciliation de '{root}' impossible : {ex.Message}");
        }
        finally
        {
            _indexLock.Release();
        }
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
            await StartIndexingAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.Info($"[SmartIndex] Cache chargé avec {_cache.Count} éléments. Vérification des changements...");

        var settings = _settingsProvider.Current;

        // Le calcul des empreintes parcourt tous les dossiers indexés : jamais sur le thread UI.
        var comparison = await Task.Run(() => _fingerprintService.CompareWithStored(
            settings.Search.IndexedFolders,
            settings.Search.FileExtensions,
            settings.Search.SearchDepth,
            settings.Search.IndexHiddenFolders), cancellationToken).ConfigureAwait(false);

        if (!comparison.HasChanges)
        {
            _logger.Info("[SmartIndex] Aucun changement de dossiers — rafraîchissement des sources volatiles uniquement...");

            // Toujours rafraîchir les sources volatiles (Store apps, bookmarks, paramètres Windows)
            // même si les dossiers n'ont pas changé, pour garantir qu'elles sont à jour.
            await RefreshVolatileSourcesAsync(settings, cancellationToken).ConfigureAwait(false);

            IndexingCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        _logger.Info($"[SmartIndex] Changements: {comparison.NewFolders.Count} nouveaux, " +
                     $"{comparison.ModifiedFolders.Count} modifiés, {comparison.DeletedFolders.Count} supprimés");

        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IsIndexing = true;
            _indexingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IndexingStarted?.Invoke(this, EventArgs.Empty);
            var token = _indexingCts.Token;

            // 1. Racines retirées de la configuration ou disparues : aucune passe ne les reverra
            foreach (var deletedFolder in comparison.DeletedFolders)
            {
                RemoveItemsByFolder(deletedFolder);
                _fingerprintService.RemoveFingerprint(deletedFolder);
            }

            // 2. Racines nouvelles ou modifiées : rescannées SANS être vidées d'abord.
            //    UPSERT (usage conservé) puis balayage limité à ces racines.
            var foldersToIndex = comparison.NewFolders
                .Concat(comparison.ModifiedFolders)
                .Where(Directory.Exists)
                .ToList();

            if (foldersToIndex.Count > 0)
            {
                var runStamp = NewIndexStamp();
                var items = new ConcurrentBag<SearchResult>();
                var unreachableRoots = new ConcurrentBag<string>();

                var folderTasks = foldersToIndex
                    .Select(folder => Task.Run(() =>
                    {
                        if (!IndexFolder(folder, items, settings, token))
                            unreachableRoots.Add(folder);
                    }, token))
                    .ToArray();

                await Task.WhenAll(folderTasks).ConfigureAwait(false);

                var newItems = items
                    .GroupBy(i => DeduplicationHelper.GetIdentityKey(i))
                    .Select(g => g.First())
                    .ToList();

                await SaveToDatabaseAsync(newItems, runStamp, token).ConfigureAwait(false);

                // Portée du balayage : éléments dont la racine la plus proche a été rescannée
                // avec succès (une racine imbriquée non modifiée n'est pas touchée).
                var allRoots = settings.Search.IndexedFolders.ToList();
                var scanned = foldersToIndex
                    .Select(IndexRules.NormalizeDirectory)
                    .Except(unreachableRoots.Select(IndexRules.NormalizeDirectory), StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                await SweepAsync(runStamp,
                    path => IndexRules.FindRoot(path, allRoots) is { } root && scanned.Contains(root),
                    token).ConfigureAwait(false);

                LoadCacheFromDatabase();
                _logger.Info($"[SmartIndex] {newItems.Count} éléments réindexés");
            }

            // 3. Réindexer les bookmarks et Store apps (rapide, toujours frais)
            await RefreshVolatileSourcesAsync(settings, token).ConfigureAwait(false);

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
        
        await Task.WhenAll(storeTask, bookmarksTask).ConfigureAwait(false);
        
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
                await conn.OpenAsync(token).ConfigureAwait(false);
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
                var purged = await deleteCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                _logger.Info($"[Volatile] Purgé {purged} entrées volatiles périmées de la DB");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Erreur purge volatiles: {ex.Message}");
        }
        
        // Sauvegarder les items frais (UPSERT : l'usage des apps et favoris est conservé)
        var newItems = items.ToList();
        if (newItems.Count > 0)
        {
            await SaveToDatabaseAsync(newItems, NewIndexStamp(), token).ConfigureAwait(false);
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

