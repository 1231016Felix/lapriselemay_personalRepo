using System.Collections.Concurrent;
using System.IO;
using QuickLauncher.Models;

using Timer = System.Threading.Timer;

namespace QuickLauncher.Services;

/// <summary>
/// Service de surveillance des fichiers pour l'indexation incrémentale.
/// Détecte les créations, modifications et suppressions de fichiers.
/// </summary>
public sealed class FileWatcherService : IDisposable
{
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentQueue<FileChangeEvent> _changeQueue = new();
    private readonly ILogger _logger;
    private readonly Timer _processTimer;
    private readonly object _processLock = new();
    
    private readonly ISettingsProvider _settingsProvider;
    private bool _disposed;
    private bool _isProcessing;

    /// <summary>
    /// Événement déclenché quand des fichiers ont changé.
    /// </summary>
    public event EventHandler<FileChangesEventArgs>? FilesChanged;

    /// <summary>
    /// Nombre de changements en attente de traitement.
    /// </summary>
    public int PendingChangesCount => _changeQueue.Count;

    public FileWatcherService(ISettingsProvider settingsProvider, ILogger? logger = null)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _logger = logger ?? new FileLogger(Constants.AppName, Constants.LogFileName);
        
        // Timer pour traiter les changements par lots (évite le spam)
        _processTimer = new Timer(ProcessChanges, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Démarre la surveillance des dossiers indexés.
    /// </summary>
    public void Start()
    {
        var settings = _settingsProvider.Current;
        
        foreach (var folder in settings.Search.IndexedFolders.Where(Directory.Exists))
        {
            StartWatching(folder);
        }
        
        _logger.Info($"FileWatcher démarré - {_watchers.Count} dossiers surveillés");
    }

    /// <summary>
    /// Arrête toute surveillance.
    /// </summary>
    public void Stop()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        
        _logger.Info("FileWatcher arrêté");
    }

    /// <summary>
    /// Ajoute un dossier à surveiller.
    /// </summary>
    public void AddFolder(string folderPath)
    {
        if (Directory.Exists(folderPath) && !_watchers.ContainsKey(folderPath))
        {
            StartWatching(folderPath);
        }
    }

    /// <summary>
    /// Retire un dossier de la surveillance.
    /// </summary>
    public void RemoveFolder(string folderPath)
    {
        if (_watchers.TryRemove(folderPath, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private void StartWatching(string folderPath)
    {
        try
        {
            var watcher = new FileSystemWatcher(folderPath)
            {
                // Seuls les changements de nom (création / suppression / renommage) modifient
                // l'index. On n'écoute pas LastWrite/CreationTime : leur événement Changed était
                // déjà ignoré, et les inclure ne faisait qu'augmenter le risque de débordement
                // du buffer interne sur de grosses arborescences (ex: Documents).
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            // Configurer les filtres d'extensions si possible
            // Note: FileSystemWatcher ne supporte qu'un seul filtre, on utilise donc "*"
            watcher.Filter = "*.*";

            watcher.Created += OnFileCreated;
            watcher.Deleted += OnFileDeleted;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;

            _watchers[folderPath] = watcher;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Impossible de surveiller '{folderPath}': {ex.Message}");
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (ShouldProcess(e.FullPath))
        {
            EnqueueChange(FileChangeType.Created, e.FullPath);
        }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (ShouldProcessDeleted(e.FullPath))
        {
            EnqueueChange(FileChangeType.Deleted, e.FullPath);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        // L'ancien chemin n'existe plus sur le disque → utiliser la variante "deleted"
        if (ShouldProcessDeleted(e.OldFullPath))
        {
            EnqueueChange(FileChangeType.Deleted, e.OldFullPath);
        }
        if (ShouldProcess(e.FullPath))
        {
            EnqueueChange(FileChangeType.Created, e.FullPath);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.Warning($"Erreur FileWatcher: {e.GetException().Message}");
        
        // Tenter de redémarrer le watcher
        if (sender is FileSystemWatcher watcher)
        {
            var path = watcher.Path;
            RemoveFolder(path);
            
            Task.Delay(5000).ContinueWith(_ =>
            {
                if (Directory.Exists(path))
                    AddFolder(path);
            });
        }
    }

    private bool ShouldProcess(string path)
    {
        // Vérifier si c'est un fichier avec une extension indexée
        var ext = Path.GetExtension(path).ToLowerInvariant();
        
        // Ignorer les fichiers système et temporaires
        if (string.IsNullOrEmpty(ext))
            return Directory.Exists(path); // C'est un dossier
        
        if (ext.StartsWith(".tmp") || ext == ".temp" || path.Contains("~$"))
            return false;
        
        // Vérifier les extensions autorisées
        return _settingsProvider.Current.Search.FileExtensions.Contains(ext);
    }

    /// <summary>
    /// Variante de <see cref="ShouldProcess"/> pour un chemin qui vient d'être
    /// supprimé (ou renommé) : impossible d'interroger le disque, Directory.Exists
    /// est toujours faux. Un chemin sans extension est donc traité comme un dossier
    /// potentiel au lieu d'être ignoré — sinon la suppression d'un dossier indexé
    /// laissait des entrées fantômes dans l'index jusqu'à la prochaine réindexation.
    /// </summary>
    private bool ShouldProcessDeleted(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (string.IsNullOrEmpty(ext))
            return true; // Dossier potentiel

        if (ext.StartsWith(".tmp") || ext == ".temp" || path.Contains("~$"))
            return false;

        return _settingsProvider.Current.Search.FileExtensions.Contains(ext);
    }

    private void EnqueueChange(FileChangeType type, string path)
    {
        _changeQueue.Enqueue(new FileChangeEvent(type, path, DateTime.UtcNow));
        
        // Démarrer le timer pour traiter par lots (500ms de délai)
        _processTimer.Change(500, Timeout.Infinite);
    }

    private void ProcessChanges(object? state)
    {
        if (_isProcessing) return;
        
        lock (_processLock)
        {
            if (_isProcessing) return;
            _isProcessing = true;
        }

        try
        {
            // Dédupliquer en gardant le DERNIER changement pour chaque chemin.
            // Ex: Created puis Deleted → seul Deleted est conservé (pas de fichier fantôme).
            var latestByPath = new Dictionary<string, FileChangeEvent>(StringComparer.OrdinalIgnoreCase);

            while (_changeQueue.TryDequeue(out var change))
            {
                latestByPath[change.Path] = change;
            }

            var changes = latestByPath.Values.ToList();

            if (changes.Count > 0)
            {
                _logger.Info($"FileWatcher: {changes.Count} changements détectés");
                
                // Notifier les abonnés
                FilesChanged?.Invoke(this, new FileChangesEventArgs(changes));
            }
        }
        finally
        {
            _isProcessing = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _processTimer.Dispose();
        Stop();
        
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Type de changement de fichier.
/// </summary>
public enum FileChangeType
{
    Created,
    Deleted,
    Modified
}

/// <summary>
/// Événement de changement de fichier.
/// </summary>
public readonly record struct FileChangeEvent(
    FileChangeType Type,
    string Path,
    DateTime Timestamp);

/// <summary>
/// Arguments pour l'événement FilesChanged.
/// </summary>
public sealed class FileChangesEventArgs : EventArgs
{
    public IReadOnlyList<FileChangeEvent> Changes { get; }

    public FileChangesEventArgs(IReadOnlyList<FileChangeEvent> changes)
    {
        Changes = changes;
    }

    public IEnumerable<FileChangeEvent> CreatedFiles => 
        Changes.Where(c => c.Type == FileChangeType.Created);
    
    public IEnumerable<FileChangeEvent> DeletedFiles => 
        Changes.Where(c => c.Type == FileChangeType.Deleted);
}
