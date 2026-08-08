using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace QuickLauncher.Services;

/// <summary>
/// Logger qui écrit les messages dans un fichier dans le dossier AppData local.
/// Thread-safe avec écriture asynchrone en arrière-plan.
/// 
/// Rotation automatique : au démarrage, supprime les fichiers de log
/// de plus de <see cref="MaxLogAgeDays"/> jours pour éviter l'accumulation sur disque.
/// </summary>
public sealed class FileLogger : ILogger, IDisposable
{
    private readonly string _logFilePath;
    private readonly BlockingCollection<string> _logQueue = new(1000);
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    
    /// <summary>Durée de rétention des fichiers de log en jours.</summary>
    private const int MaxLogAgeDays = 7;

    public FileLogger(string appName)
        : this(appName, $"{appName}_{DateTime.Now:yyyy-MM-dd}.log")
    {
    }

    public FileLogger(string appName, string logFileName)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName,
            "Logs");
        
        Directory.CreateDirectory(logDir);
        _logFilePath = Path.Combine(logDir, logFileName);
        
        // Nettoyage des vieux logs au démarrage (fire-and-forget, ne bloque pas l'init)
        _ = Task.Run(() => PurgeOldLogs(logDir));
        
        _writerTask = Task.Run(ProcessLogQueueAsync);
    }
    
    /// <summary>
    /// Supprime les fichiers .log de plus de <see cref="MaxLogAgeDays"/> jours.
    /// Exécuté en arrière-plan au démarrage pour ne pas ralentir l'initialisation.
    /// </summary>
    private static void PurgeOldLogs(string logDir)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-MaxLogAgeDays);
            var logFiles = Directory.GetFiles(logDir, "*.log");
            var deletedCount = 0;
            
            foreach (var file in logFiles)
            {
                try
                {
                    var lastWrite = File.GetLastWriteTime(file);
                    if (lastWrite < cutoff)
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                }
                catch
                {
                    // Fichier verrouillé ou permission refusée — ignorer
                }
            }
            
            if (deletedCount > 0)
                System.Diagnostics.Debug.WriteLine($"[FileLogger] Purgé {deletedCount} fichier(s) de log de plus de {MaxLogAgeDays} jours");
        }
        catch
        {
            // Le nettoyage ne doit jamais faire crasher l'app
        }
    }

    public void Debug(string message) => Log("DEBUG", message);
    public void Info(string message) => Log("INFO", message);
    public void Warning(string message) => Log("WARN", message);
    
    public void Error(string message, Exception? exception = null)
    {
        var sb = new StringBuilder();
        sb.Append(message);
        
        if (exception != null)
        {
            sb.AppendLine();
            sb.Append("  Exception: ").AppendLine(exception.GetType().Name);
            sb.Append("  Message: ").AppendLine(exception.Message);
            if (exception.StackTrace != null)
            {
                sb.AppendLine("  StackTrace: " + exception.StackTrace);
            }
            
            // Les exceptions les plus utiles (TypeInitializationException,
            // TargetInvocationException, AggregateException) n'ont d'intérêt
            // que par leur cause racine : on déroule toute la chaîne.
            var inner = exception.InnerException;
            var depth = 1;
            while (inner != null && depth <= 5)
            {
                sb.Append("  --- Inner #").Append(depth).Append(": ").AppendLine(inner.GetType().FullName);
                sb.Append("      Message: ").AppendLine(inner.Message);
                if (inner.StackTrace != null)
                {
                    sb.Append("      StackTrace: ").AppendLine(inner.StackTrace);
                }
                inner = inner.InnerException;
                depth++;
            }
        }
        
        Log("ERROR", sb.ToString());
    }

    private void Log(string level, string message)
    {
        if (_disposed) return;
        
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var threadId = Environment.CurrentManagedThreadId;
        var logLine = $"[{timestamp}] [{level,-5}] [T{threadId:D3}] {message}";
        
        try
        {
            _logQueue.TryAdd(logLine, 100);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task ProcessLogQueueAsync()
    {
        try
        {
            await using var writer = new StreamWriter(_logFilePath, append: true, Encoding.UTF8)
            {
                AutoFlush = true
            };
            
            foreach (var logLine in _logQueue.GetConsumingEnumerable(_cts.Token))
            {
                await writer.WriteLineAsync(logLine);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _logQueue.CompleteAdding();
        _cts.Cancel();
        
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        
        _cts.Dispose();
        _logQueue.Dispose();
    }
}
