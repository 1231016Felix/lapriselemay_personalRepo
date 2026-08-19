namespace QuickLauncher.Services;

/// <summary>
/// Pont statique vers le logger applicatif pour le code qui ne peut pas recevoir
/// <see cref="ILogger"/> par injection (classes statiques comme SecureStorageService
/// ou AppSettings).
///
/// Branché par App.OnStartup dès le démarrage. Tant qu'aucun logger n'est affecté
/// (tout début du démarrage, tests unitaires), les messages tombent sur
/// Debug.WriteLine pour rester visibles en debug sans rien casser.
/// </summary>
public static class Log
{
    /// <summary>Logger applicatif partagé. Affecté une seule fois au démarrage.</summary>
    public static ILogger? Logger { get; set; }

    public static void Debug(string message)
    {
        if (Logger != null) Logger.Debug(message);
        else System.Diagnostics.Debug.WriteLine(message);
    }

    public static void Info(string message)
    {
        if (Logger != null) Logger.Info(message);
        else System.Diagnostics.Debug.WriteLine(message);
    }

    public static void Warning(string message)
    {
        if (Logger != null) Logger.Warning(message);
        else System.Diagnostics.Debug.WriteLine(message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        if (Logger != null) Logger.Error(message, exception);
        else System.Diagnostics.Debug.WriteLine($"{message} — {exception}");
    }
}
