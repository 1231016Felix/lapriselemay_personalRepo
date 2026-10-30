using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Interface pour l'accès centralisé aux paramètres de l'application.
/// Garde les settings en mémoire et notifie les abonnés lors des changements.
/// Élimine les appels répétés à AppSettings.Load() (lecture disque à chaque frappe).
/// </summary>
public interface ISettingsProvider
{
    /// <summary>
    /// Paramètres actuels en mémoire (jamais null après initialisation).
    /// </summary>
    AppSettings Current { get; }
    
    /// <summary>
    /// Événement déclenché après chaque sauvegarde/rechargement des paramètres.
    /// Les abonnés reçoivent les nouveaux paramètres sans relire le disque.
    /// </summary>
    event EventHandler<AppSettings>? SettingsChanged;
    
    /// <summary>
    /// Sauvegarde les paramètres actuels sur disque et notifie les abonnés.
    /// </summary>
    /// <returns><c>true</c> si l'écriture a réussi.</returns>
    bool Save();
    
    /// <summary>
    /// Force un rechargement depuis le disque (utile après modification externe).
    /// Notifie les abonnés avec les nouvelles valeurs.
    /// </summary>
    void Reload();
    
    /// <summary>
    /// Applique une modification aux paramètres via clone-swap et sauvegarde.
    /// Thread-safe : les lecteurs concurrents continuent de voir un snapshot stable.
    /// </summary>
    /// <param name="updateAction">Action de modification sur les paramètres.</param>
    /// <returns><c>true</c> si la nouvelle version a été écrite sur disque.</returns>
    bool Update(Action<AppSettings> updateAction);
}
