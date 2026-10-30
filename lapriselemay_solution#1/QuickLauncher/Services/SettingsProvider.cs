using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Implémentation de <see cref="ISettingsProvider"/> avec cache en mémoire
/// et pattern clone-swap pour la thread safety.
/// 
/// Remplace les appels dispersés à AppSettings.Load() qui lisaient le disque
/// à chaque recherche (hot path critique pour la performance).
/// 
/// <b>Contrat de threading :</b>
/// <list type="bullet">
///   <item><see cref="Current"/> retourne une référence atomique (volatile) et peut être
///         lu depuis n'importe quel thread. L'objet retourné est un snapshot stable :
///         il ne sera jamais muté après publication.</item>
///   <item><see cref="Update"/> clone l'objet courant, applique la mutation sur le clone,
///         puis swap la référence atomiquement. Les threads de recherche en cours
///         continuent de lire l'ancien snapshot sans interférence.</item>
///   <item><see cref="Save"/>, <see cref="Reload"/> doivent être appelés depuis le UI thread.</item>
/// </list>
/// </summary>
public sealed class SettingsProvider : ISettingsProvider
{
    /// <summary>
    /// Référence volatile : les lectures/écritures de la référence sont atomiques et
    /// visibles immédiatement par tous les threads sans lock.
    /// L'objet pointé n'est jamais muté après publication (clone-swap).
    /// </summary>
    private volatile AppSettings _current;

    /// <summary>
    /// Verrou protégeant <see cref="Update"/> contre les appels concurrents.
    /// Sans ce verrou, deux Update() simultanés clonent le même _current
    /// et le second écrase les modifications du premier (lost update).
    /// </summary>
    private readonly object _updateLock = new();

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? SettingsChanged;

    public SettingsProvider()
    {
        _current = AppSettings.Load();
        Log.Debug($"[SettingsProvider] Initialisé avec {_current.Search.IndexedFolders.Count} dossiers indexés");
    }

    public bool Save()
    {
        var saved = _current.Save();
        Log.Debug(saved
            ? "[SettingsProvider] Sauvegardé sur disque et notification envoyée"
            : "[SettingsProvider] ÉCHEC de la sauvegarde sur disque");
        SettingsChanged?.Invoke(this, _current);
        return saved;
    }

    public void Reload()
    {
        _current = AppSettings.Load();
        Log.Debug("[SettingsProvider] Rechargé depuis le disque");
        SettingsChanged?.Invoke(this, _current);
    }

    /// <summary>
    /// Applique une modification aux paramètres via le pattern clone-swap :
    /// 1. Clone l'objet courant (copie profonde)
    /// 2. Applique la mutation sur le clone
    /// 3. Sauvegarde le clone sur disque
    /// 4. Swap la référence volatile (publication atomique)
    /// 
    /// Protégé par un verrou pour empêcher les lost updates :
    /// sans lock, deux appels concurrents cloneraient le même _current
    /// et le dernier à écrire écraserait les modifications du premier.
    /// 
    /// L'événement <see cref="SettingsChanged"/> est déclenché hors du verrou
    /// pour éviter les risques de deadlock si un abonné tente un Update() en réaction.
    /// 
    /// Si <paramref name="updateAction"/> lève une exception, rien n'est publié :
    /// le snapshot courant reste inchangé.
    /// </summary>
    /// <returns>
    /// <c>true</c> si la nouvelle version a été écrite sur disque. En cas d'échec d'écriture,
    /// la modification reste appliquée en mémoire pour la session mais <c>false</c> est
    /// retourné afin que l'appelant puisse le signaler.
    /// </returns>
    public bool Update(Action<AppSettings> updateAction)
    {
        AppSettings snapshot;
        bool saved;
        lock (_updateLock)
        {
            var clone = _current.Clone();
            updateAction(clone);
            saved = clone.Save();
            _current = clone;
            snapshot = clone;
        }
        SettingsChanged?.Invoke(this, snapshot);
        return saved;
    }
}