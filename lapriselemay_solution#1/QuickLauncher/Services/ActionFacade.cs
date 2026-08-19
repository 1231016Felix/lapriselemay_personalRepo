using QuickLauncher.Models;
using QuickLauncher.Services.CommandHandlers;

namespace QuickLauncher.Services;

/// <summary>
/// Façade regroupant les services d'actions et de lancement utilisés par le ViewModel.
/// Réduit le nombre de dépendances directes du ViewModel (de 17 à 5 paramètres).
/// 
/// Regroupe : PinnedItemsManager, ResultActionService, ILaunchService,
/// IFileActionProvider, ISystemControlExecutor.
/// </summary>
public sealed class ActionFacade
{
    private readonly PinnedItemsManager _pinnedItemsManager;
    private readonly ResultActionService _resultActionService;
    private readonly ILaunchService _launchService;
    private readonly IFileActionProvider _fileActionProvider;
    private readonly ISystemControlExecutor _systemControlExecutor;

    public ActionFacade(
        PinnedItemsManager pinnedItemsManager,
        ResultActionService resultActionService,
        ILaunchService launchService,
        IFileActionProvider fileActionProvider,
        ISystemControlExecutor systemControlExecutor)
    {
        _pinnedItemsManager = pinnedItemsManager ?? throw new ArgumentNullException(nameof(pinnedItemsManager));
        _resultActionService = resultActionService ?? throw new ArgumentNullException(nameof(resultActionService));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _fileActionProvider = fileActionProvider ?? throw new ArgumentNullException(nameof(fileActionProvider));
        _systemControlExecutor = systemControlExecutor ?? throw new ArgumentNullException(nameof(systemControlExecutor));
    }

    // === PinnedItemsManager ===

    /// <summary>Nombre d'items épinglés.</summary>
    public int PinnedCount => _pinnedItemsManager.Count;

    /// <summary>Vérifie si un chemin est épinglé.</summary>
    public bool IsPinned(string path) => _pinnedItemsManager.IsPinned(path);

    /// <summary>Vérifie si un résultat à l'index donné est un item épinglé.</summary>
    public bool IsResultPinned(int resultIndex, int resultCount)
        => _pinnedItemsManager.IsResultPinned(resultIndex, resultCount);

    /// <summary>Retourne les items épinglés convertis en SearchResult.</summary>
    public List<SearchResult> GetPinnedResults() => _pinnedItemsManager.GetPinnedResults();

    /// <summary>Réordonne un item épinglé par drag &amp; drop.</summary>
    public void ReorderPinned(int fromIndex, int toIndex) => _pinnedItemsManager.Reorder(fromIndex, toIndex);

    // === ResultActionService ===

    /// <summary>
    /// Exécute une action sur un résultat de recherche.
    /// Retourne un ActionOutcome déclaratif que le ViewModel interprète.
    /// </summary>
    public ActionOutcome ExecuteAction(FileAction action, SearchResult result, bool isSearchEmpty)
        => _resultActionService.Execute(action, result, isSearchEmpty);

    /// <summary>Vérifie si un chemin cible possède un alias.</summary>
    public bool HasAlias(string targetPath) => _resultActionService.HasAlias(targetPath);

    /// <summary>Enregistre un alias.</summary>
    /// <returns>Message de notification.</returns>
    public string SaveAlias(string alias, string targetPath) => _resultActionService.SaveAlias(alias, targetPath);

    /// <summary>Supprime l'alias associé à un chemin cible.</summary>
    /// <returns>Message de notification, ou null si aucun alias trouvé.</returns>
    public string? DeleteAlias(string targetPath) => _resultActionService.DeleteAlias(targetPath);

    // === ILaunchService ===

    /// <summary>Lance un item (application, fichier, URL, etc.).</summary>
    /// <returns>false si le lancement a échoué (l'échec est déjà journalisé).</returns>
    public bool Launch(SearchResult item) => _launchService.Launch(item);

    /// <summary>Lance un item en mode administrateur (élévation UAC).</summary>
    /// <returns>false si le lancement a échoué (l'échec est déjà journalisé).</returns>
    public bool RunAsAdmin(SearchResult item) => _launchService.RunAsAdmin(item);

    // === IFileActionProvider ===

    /// <summary>Retourne les actions disponibles pour un résultat donné.</summary>
    public List<FileAction> GetActionsForResult(SearchResult result, bool isPinned, bool hasAlias)
        => _fileActionProvider.GetActionsForResult(result, isPinned, hasAlias);

    // === ISystemControlExecutor ===

    /// <summary>
    /// Tente d'exécuter une commande de contrôle système.
    /// Async : la commande wifi attend des API WinRT sans bloquer le thread UI.
    /// </summary>
    public Task<SystemControlExecutionResult> ExecuteSystemControlAsync(string command)
        => _systemControlExecutor.ExecuteAsync(command);
}
