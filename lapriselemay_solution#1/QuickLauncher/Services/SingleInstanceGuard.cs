using System.Threading;

namespace QuickLauncher.Services;

/// <summary>
/// Garantit une seule instance de QuickLauncher par session Windows. Une deuxième ouverture
/// réveille l'instance existante (qui affiche le launcher) puis se termine, au lieu de se
/// disputer le raccourci global et d'écrire en parallèle dans settings.json, aliases.json
/// et index.db.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\LapriseLemay.QuickLauncher.SingleInstance";
    private const string ShowEventName = @"Local\LapriseLemay.QuickLauncher.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
    }

    /// <summary>
    /// Tente de devenir l'instance principale. Si une autre instance tourne, elle est réveillée
    /// immédiatement ; on attend ensuite brièvement qu'elle se termine (cas d'un redémarrage :
    /// mise à jour Velopack, « Redémarrer » dans les paramètres) avant de conclure.
    /// </summary>
    /// <returns>Le garde à conserver pendant toute la vie de l'application, ou <c>null</c>
    /// si une autre instance reste active (le processus courant doit alors se terminer).</returns>
    public static SingleInstanceGuard? TryAcquire(TimeSpan restartGracePeriod)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero))
                return new SingleInstanceGuard(mutex);

            SignalExistingInstance();

            if (mutex.WaitOne(restartGracePeriod))
                return new SingleInstanceGuard(mutex);
        }
        catch (AbandonedMutexException)
        {
            // L'instance précédente s'est arrêtée brutalement : le mutex nous revient
            return new SingleInstanceGuard(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Appelle <paramref name="onShowRequested"/> (sur un thread du pool) chaque fois qu'un
    /// deuxième lancement demande l'affichage du launcher.
    /// </summary>
    public void ListenForShowRequests(Action onShowRequested)
    {
        _registration?.Unregister(null);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
            showEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // L'instance existante n'écoute pas (encore) : rien à réveiller
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _registration?.Unregister(null);
        _showEvent.Dispose();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* déjà relâché ou pas sur ce thread */ }
        _mutex.Dispose();
    }
}
