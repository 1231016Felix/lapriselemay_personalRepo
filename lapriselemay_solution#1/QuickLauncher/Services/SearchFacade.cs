using QuickLauncher.Models;
using QuickLauncher.Services.CommandHandlers;

namespace QuickLauncher.Services;

/// <summary>
/// Façade regroupant les services de recherche utilisés par le ViewModel.
/// Réduit le nombre de dépendances directes du ViewModel (de 17 à 5 paramètres).
/// 
/// Regroupe : SearchService, GhostSuggestionService, SystemControlSuggestionService,
/// CommandRouter, IIconLoader, AliasService.
/// </summary>
public sealed class SearchFacade : IDisposable
{
    private readonly SearchService _searchService;
    private readonly GhostSuggestionService _ghostSuggestionService;
    private readonly SystemControlSuggestionService _systemControlService;
    private readonly CommandRouter _commandRouter;
    private readonly IIconLoader _iconLoader;
    private readonly AliasService _aliasService;

    public SearchFacade(
        SearchService searchService,
        GhostSuggestionService ghostSuggestionService,
        SystemControlSuggestionService systemControlService,
        CommandRouter commandRouter,
        IIconLoader iconLoader,
        AliasService aliasService)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _ghostSuggestionService = ghostSuggestionService ?? throw new ArgumentNullException(nameof(ghostSuggestionService));
        _systemControlService = systemControlService ?? throw new ArgumentNullException(nameof(systemControlService));
        _commandRouter = commandRouter ?? throw new ArgumentNullException(nameof(commandRouter));
        _iconLoader = iconLoader ?? throw new ArgumentNullException(nameof(iconLoader));
        _aliasService = aliasService ?? throw new ArgumentNullException(nameof(aliasService));
    }

    // === SearchService ===

    /// <summary>
    /// Recherche des résultats dans le cache indexé (scoring, filtrage, déduplication).
    /// </summary>
    public List<SearchResult> Search(string query, CancellationToken token = default)
        => _searchService.Search(query, token);

    // === GhostSuggestionService ===

    /// <summary>
    /// Calcule la suggestion fantôme pour le texte de recherche courant.
    /// </summary>
    public string GetGhostSuggestion(string searchText, IReadOnlyList<SearchResult> results)
        => _ghostSuggestionService.GetSuggestion(searchText, results);

    // === SystemControlSuggestionService ===

    /// <summary>
    /// Vérifie si la requête correspond à une commande de contrôle système.
    /// </summary>
    public bool IsSystemControlCommand(string query) => _systemControlService.IsSystemControlCommand(query);

    /// <summary>
    /// Construit les suggestions de commandes système pour la requête.
    /// </summary>
    public List<SearchResult> BuildControlSuggestions(string query) => _systemControlService.BuildSuggestions(query);

    /// <summary>
    /// Construit la liste des commandes d'aide (:help).
    /// </summary>
    public List<SearchResult> BuildHelpResults() => _systemControlService.BuildHelpResults();

    // === CommandRouter ===

    /// <summary>
    /// Tente de router la requête vers un handler spécialisé (météo, traduction, IA, recherche système).
    /// </summary>
    public ICommandHandler? FindCommandHandler(string query) => _commandRouter.FindHandler(query);

    // === IIconLoader ===

    /// <summary>
    /// Charge les icônes natives en arrière-plan pour les résultats de recherche.
    /// </summary>
    public Task LoadIconsAsync(IReadOnlyList<SearchResult> results, CancellationToken cancellationToken = default)
        => _iconLoader.LoadIconsAsync(results, cancellationToken);

    // === AliasService ===

    /// <summary>
    /// Recherche des alias correspondant à la requête.
    /// </summary>
    public List<SearchResult> SearchAliases(string query) => _aliasService.Search(query);

    public void Dispose()
    {
        _aliasService?.Dispose();
    }
}
