using System.IO;
using Velopack;
using Velopack.Sources;

namespace QuickLauncher.Services;

/// <summary>
/// Gère les mises à jour automatiques via Velopack, en s'appuyant sur les
/// GitHub Releases du dépôt comme source de distribution.
///
/// L'app n'est « installée » que lorsqu'elle est lancée depuis le dossier
/// Velopack (%LocalAppData%\QuickLauncher). En développement (F5 dans Visual
/// Studio), <see cref="UpdateManager.IsInstalled"/> vaut false et toutes les
/// opérations sont ignorées silencieusement.
/// </summary>
public sealed class UpdateService
{
    private readonly ILogger _logger;
    private readonly UpdateManager _manager;
    private UpdateInfo? _pendingUpdate;

    public UpdateService(ILogger logger)
    {
        _logger = logger;
        _manager = new UpdateManager(new GithubSource(Constants.UpdateRepositoryUrl, null, prerelease: false));
    }

    /// <summary>Vrai si l'app tourne depuis une installation Velopack.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>Version actuellement installée, ou null hors installation Velopack.</summary>
    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    /// <summary>Vrai si une mise à jour a été téléchargée et attend un redémarrage.</summary>
    public bool HasPendingUpdate => _pendingUpdate is not null;

    /// <summary>
    /// Vérifie la présence d'une mise à jour et la télécharge en arrière-plan.
    /// Retourne le numéro de la nouvelle version, ou null si l'app est à jour.
    /// N'applique rien : l'installation se fait au redémarrage via
    /// <see cref="ApplyPendingUpdateAndRestart"/>.
    /// </summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (!_manager.IsInstalled)
        {
            _logger.Debug("[Update] Ignoré : l'app ne tourne pas depuis une installation Velopack");
            return null;
        }

        try
        {
            _logger.Info("[Update] Vérification des mises à jour...");
            var update = await _manager.CheckForUpdatesAsync();

            if (update is null)
            {
                _logger.Info($"[Update] Aucune mise à jour (version actuelle {CurrentVersion})");
                return null;
            }

            var newVersion = update.TargetFullRelease.Version.ToString();
            _logger.Info($"[Update] Version {newVersion} disponible, téléchargement...");

            await _manager.DownloadUpdatesAsync(update);
            _pendingUpdate = update;

            _logger.Info($"[Update] Version {newVersion} prête, sera appliquée au prochain redémarrage");
            return newVersion;
        }
        catch (Exception ex)
        {
            // Une panne réseau ou un repo injoignable ne doit jamais empêcher
            // l'app de fonctionner : on log et on continue.
            _logger.Warning($"[Update] Échec de la vérification : {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Applique la mise à jour téléchargée et redémarre l'application.
    /// Sans effet si aucune mise à jour n'est en attente.
    /// </summary>
    public void ApplyPendingUpdateAndRestart()
    {
        if (_pendingUpdate is null) return;

        _logger.Info("[Update] Application de la mise à jour et redémarrage...");
        _manager.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    /// <summary>
    /// Appelé une seule fois, juste après l'installation initiale.
    /// Purge un éventuel index SQLite laissé par une installation précédente
    /// afin que la première indexation reparte sur une base saine.
    /// </summary>
    public static void OnFirstRun()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Constants.AppName);

            var staleIndex = Path.Combine(dataDir, Constants.DatabaseFileName);
            if (File.Exists(staleIndex))
                File.Delete(staleIndex);
        }
        catch
        {
            // Best-effort : un échec ici ne doit pas bloquer le premier lancement.
        }
    }
}
