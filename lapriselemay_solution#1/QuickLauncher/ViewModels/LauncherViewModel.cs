using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickLauncher.Models;
using QuickLauncher.Services;
using QuickLauncher.Services.CommandHandlers;

namespace QuickLauncher.ViewModels;

/// <summary>
/// ViewModel pour la fenêtre principale avec commandes système optimisées.
/// 
/// Les 17 dépendances d'origine sont regroupées en deux façades :
/// - <see cref="SearchFacade"/> : recherche, scoring, suggestions, commandes async, icônes, alias
/// - <see cref="ActionFacade"/> : épingles, actions, lancement, contrôle système
/// </summary>
public sealed partial class LauncherViewModel : ObservableObject, IDisposable
{
    private readonly IndexingService _indexingService;
    private readonly ISettingsProvider _settingsProvider;
    private readonly SearchFacade _search;
    private readonly ActionFacade _actions;
    private readonly FileWatcherService? _fileWatcherService;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _debounceCts;

    /// <summary>
    /// Annulation dédiée à la prévisualisation. Sans elle, une navigation rapide
    /// (flèches maintenues) empile des générations de preview qui se terminent dans
    /// le désordre : c'est la dernière TERMINÉE qui s'affichait, pas la dernière SÉLECTIONNÉE.
    /// </summary>
    private CancellationTokenSource? _previewCts;

    private bool _disposed;
    
    /// <summary>
    /// Compteur de génération pour invalider les résultats async périmés.
    /// Incrémenté à chaque nouvelle recherche pour que DispatchCommandAsync
    /// ne puisse pas écraser les résultats d'une recherche plus récente.
    /// </summary>
    private int _searchGeneration;
    
    [ObservableProperty]
    private string _searchText = string.Empty;
    
    [ObservableProperty]
    private int _selectedIndex;
    
    [ObservableProperty]
    private bool _hasResults;
    
    [ObservableProperty]
    private bool _isIndexing;
    
    [ObservableProperty]
    private bool _isSearching;
    
    [ObservableProperty]
    private FilePreview? _currentPreview;
    
    [ObservableProperty]
    private bool _showPreviewPanel;
    
    [ObservableProperty]
    private bool _showActionsPanel;
    
    [ObservableProperty]
    private int _selectedActionIndex;
    
    [ObservableProperty]
    private string _ghostSuggestionText = string.Empty;
    
    [ObservableProperty]
    private bool _showCategoryBadges;
    
    [ObservableProperty]
    private bool _showSettingsButton;
    
    [ObservableProperty]
    private bool _showShortcutHints;
    
    public BatchObservableCollection<SearchResult> Results { get; } = [];
    public ObservableCollection<FileAction> AvailableActions { get; } = [];
    
    public event EventHandler? RequestHide;
    public event EventHandler? RequestOpenSettings;
    public event EventHandler? RequestQuit;
    public event EventHandler? RequestReindex;
    public event EventHandler<string>? RequestRename;
    public event EventHandler<string>? ShowNotification;
    public event EventHandler? RequestCaretAtEnd;
    public event EventHandler<string?>? RequestScreenCapture;
    public event EventHandler<(string Name, string Path)>? RequestCreateAlias;
    public event EventHandler<SearchResult>? RequestDeleteConfirmation;


    /// <summary>
    /// Accès rapide aux paramètres actuels (lecture seule, toujours à jour).
    /// Pour les mutations, utiliser _settingsProvider.Update() ou _settingsProvider.Save().
    /// </summary>
    private AppSettings _settings => _settingsProvider.Current;

    public LauncherViewModel(
        IndexingService indexingService,
        ISettingsProvider settingsProvider,
        SearchFacade search,
        ActionFacade actions,
        FileWatcherService? fileWatcherService = null)
    {
        _indexingService = indexingService ?? throw new ArgumentNullException(nameof(indexingService));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        
        // Initialiser les propriétés d'apparence depuis les settings
        ShowCategoryBadges = _settings.Appearance.ShowCategoryBadges;
        ShowSettingsButton = _settings.Appearance.ShowSettingsButton;
        ShowShortcutHints = _settings.Appearance.ShowShortcutHints;
        
        _indexingService.IndexingStarted += (_, _) => IsIndexing = true;
        _indexingService.IndexingCompleted += (_, _) => IsIndexing = false;
        
        // FileWatcher (injecté via DI, optionnel)
        _fileWatcherService = fileWatcherService;
        if (_fileWatcherService != null)
            _fileWatcherService.FilesChanged += OnFilesChanged;
    }

    partial void OnSearchTextChanged(string value)
    {
        // Effacer le ghost immédiatement si le texte ne correspond plus
        // (évite un flash de suggestion périmée pendant le debounce)
        if (!string.IsNullOrEmpty(GhostSuggestionText)
            && !GhostSuggestionText.StartsWith(value, StringComparison.OrdinalIgnoreCase))
        {
            GhostSuggestionText = string.Empty;
        }
        
        // Annuler le debounce précédent et en démarrer un nouveau.
        // Dispose après Cancel : la tâche en vol ne fait que catcher l'OCE et lire
        // IsCancellationRequested, deux opérations sûres sur une source disposée.
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        _ = DebounceSearchAsync(value, _debounceCts.Token);
    }
    
    /// <summary>
    /// Debounce async : attend un court délai avant de lancer la recherche.
    /// Utilise un délai allongé pour les commandes IA afin de laisser
    /// l'utilisateur finir sa question avant d'envoyer la requête.
    /// </summary>
    private async Task DebounceSearchAsync(string text, CancellationToken token)
    {
        try
        {
            var delayMs = Constants.SearchDebounceMs;
            
            // Délai allongé pour les commandes IA (configurable 1-4s)
            if (IsAiQuery(text))
            {
                var seconds = Math.Clamp(
                    _settings.Integrations.AiDebounceSeconds,
                    Constants.AiDebounceSecondsMin,
                    Constants.AiDebounceSecondsMax);
                delayMs = seconds * 1000;
                
                // Afficher un indicateur d'attente immédiat pour que l'utilisateur
                // sache que le mode IA est actif et attend la fin de la saisie
                ShowAiWaitingIndicator(text);
            }
            
            await Task.Delay(delayMs, token);
            // Vérifier que le texte n'a pas changé pendant le debounce
            if (!token.IsCancellationRequested && text == SearchText)
                await UpdateResultsInternalAsync();
        }
        catch (OperationCanceledException)
        {
            // Debounce annulé par une nouvelle frappe, c'est normal
        }
    }
    
    /// <summary>
    /// Vérifie si la requête correspond à une commande IA avec un argument.
    /// Ex: ":ai qu'est-ce qu'une API?" → true, ":ai" seul → false.
    /// </summary>
    private bool IsAiQuery(string text)
    {
        var cmd = _settings.SystemCommands.FirstOrDefault(c => c.Type == SystemControlType.AiChat);
        if (cmd is not { IsEnabled: true }) return false;
        var prefix = $":{cmd.Prefix} ";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && text.Length > prefix.Length + 1;
    }
    
    /// <summary>
    /// Affiche un indicateur visuel pendant le délai d'attente IA.
    /// L'utilisateur voit qu'il est en mode IA et que le système
    /// attend la fin de sa saisie avant d'envoyer la requête.
    /// </summary>
    private void ShowAiWaitingIndicator(string text)
    {
        var delaySeconds = Math.Clamp(
            _settings.Integrations.AiDebounceSeconds,
            Constants.AiDebounceSecondsMin,
            Constants.AiDebounceSecondsMax);
        
        Results.Clear();
        Results.Add(new SearchResult
        {
            Name = "🤖 En attente de la fin de la saisie...",
            Description = $"La requête sera envoyée {delaySeconds}s après la dernière touche",
            Type = ResultType.SystemControl,
            DisplayIcon = "✍️",
            IsInfoBlock = true
        });
        FinalizeResults();
    }
    
    partial void OnSelectedIndexChanged(int value) => RefreshSelectionDependents(value);

    /// <summary>
    /// Recalcule tout ce qui dépend de l'item sélectionné (prévisualisation, actions).
    ///
    /// Appelé par <see cref="OnSelectedIndexChanged"/>, mais aussi explicitement par
    /// <see cref="FinalizeResults"/> : après une frappe, la sélection retombe sur l'index 0
    /// qui est souvent déjà la valeur courante — la notification de changement ne part donc
    /// pas, alors que l'item À cet index, lui, a changé.
    /// </summary>
    private void RefreshSelectionDependents(int index)
    {
        // Toute sélection précédente devient caduque : annuler sa preview en vol.
        CancelPendingPreview();

        if (index >= 0 && index < Results.Count)
        {
            if (_settings.Appearance.ShowPreviewPanel)
            {
                _previewCts = new CancellationTokenSource();
                _ = UpdatePreviewAsync(Results[index], _previewCts.Token);
            }
            else
            {
                CurrentPreview = null;
            }

            UpdateAvailableActions(Results[index]);
        }
        else
        {
            CurrentPreview = null;
            AvailableActions.Clear();
        }
    }

    /// <summary>
    /// Annule et libère la prévisualisation en cours, s'il y en a une.
    /// </summary>
    private void CancelPendingPreview()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
    }

    private async Task UpdatePreviewAsync(SearchResult result, CancellationToken token)
    {
        try
        {
            // Ne pas prévisualiser certains types
            if (result.Type is ResultType.WebSearch or ResultType.Calculator 
                or ResultType.SystemCommand or ResultType.SystemControl
                or ResultType.AppControl or ResultType.SearchHistory)
            {
                CurrentPreview = null;
                return;
            }
            
            var preview = await FilePreviewService.GeneratePreviewAsync(result.Path);

            // La sélection a changé pendant la génération (fichier volumineux, disque lent) :
            // publier maintenant afficherait la preview d'un item qui n'est plus sélectionné.
            if (token.IsCancellationRequested)
                return;

            CurrentPreview = preview;
        }
        catch
        {
            if (!token.IsCancellationRequested)
                CurrentPreview = null;
        }
    }
    
    private void UpdateAvailableActions(SearchResult result)
    {
        AvailableActions.Clear();
        var isPinned = _actions.IsPinned(result.Path);
        var hasAlias = _actions.HasAlias(result.Path);
        var actions = _actions.GetActionsForResult(result, isPinned, hasAlias);
        foreach (var action in actions)
            AvailableActions.Add(action);
        
        SelectedActionIndex = 0;
    }
    
    private void OnFilesChanged(object? sender, FileChangesEventArgs e)
    {
        // Mettre à jour l'index de manière incrémentale
        System.Diagnostics.Debug.WriteLine($"[FileWatcher] {e.Changes.Count} changements détectés");
        
        // Traiter les changements dans l'IndexingService
        _indexingService.ProcessFileChanges(e.Changes);
        
        // Rafraîchir les résultats si une recherche est en cours
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => UpdateResults());
        }
    }
    
    private void UpdateResults()
    {
        // Appel direct sans debouncing (utilisé pour les rafraîchissements forcés)
        _ = UpdateResultsInternalAsync();
    }

    private async Task UpdateResultsInternalAsync()
    {
        // Annuler toute recherche précédente et invalider les résultats async en cours.
        // Dispose après Cancel : sûr car tous les lecteurs de _searchCts sont sur le
        // thread UI et le champ pointe toujours vers la source la plus récente.
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var generation = Interlocked.Increment(ref _searchGeneration);
        
        // La liste va changer : une preview en vol porte sur un item qui ne sera
        // peut-être plus affiché. L'annuler évite qu'elle s'affiche par-dessus les nouveaux résultats.
        CancelPendingPreview();
        CurrentPreview = null;
        AvailableActions.Clear();
        // Amélioration #4 : capturer la référence settings une seule fois (cache mémoire pur)
        var settings = _settings;
        
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            ShowRecentHistory();
            return;
        }
        
        var query = SearchText.Trim();
        var queryLower = query.ToLowerInvariant();
        
        // === Commandes asynchrones spécialisées ===
        // Le CommandRouter dispatche vers le bon handler (météo, traduction, IA, recherche système).
        var handler = _search.FindCommandHandler(queryLower);
        if (handler != null)
        {
            // Les commandes async gèrent leur propre cycle Results.Clear → affichage
            Results.Clear();
            _ = DispatchCommandAsync(handler, queryLower, generation, token);
            return;
        }
        
        // Amélioration #3 : construire les résultats dans une liste temporaire
        // pour éviter le flash visuel causé par Results.Clear() suivi d'ajouts individuels.
        var tempResults = new List<SearchResult>();
        
        // Suggestion pour :find (si activée, quand l'utilisateur tape le préfixe sans argument)
        var findCmd = settings.SystemCommands.FirstOrDefault(c => c.Type == SystemControlType.SystemSearch);
        var findPrefix = findCmd?.Prefix ?? "find";
        var findEnabled = findCmd?.IsEnabled ?? true;
        
        if (findEnabled && (queryLower.StartsWith($":{findPrefix}") || $":{findPrefix}".StartsWith(queryLower)))
        {
            tempResults.Add(new SearchResult
            {
                Name = $":{findPrefix} <terme>",
                Description = findCmd?.Description ?? "Rechercher dans tout le système via Windows Search",
                Type = ResultType.SystemCommand,
                DisplayIcon = findCmd?.Icon ?? "🔍",
                Path = $":{findPrefix}"
            });
            
            if (queryLower == $":{findPrefix}")
            {
                SwapResults(tempResults);
                return;
            }
        }
        
        // Vérifier les commandes de contrôle système (inclut les commandes applicatives)
        if (_search.IsSystemControlCommand(queryLower))
        {
            var controlResults = _search.BuildControlSuggestions(queryLower);
            Results.ReplaceAll(controlResults);
            FinalizeResults();
            return;
        }
        
        // Recherche d'alias (priorité haute)
        if (settings.Search.EnableAliases)
        {
            var aliasResults = _search.SearchAliases(query);
            foreach (var alias in aliasResults.Take(3))
            {
                alias.DisplayIcon = "⌨️";
                alias.Description = $"Alias → {alias.Path}";
                tempResults.Add(alias);
            }
        }
        
        // Résultats de recherche normaux — le scoring (potentiellement coûteux sur un gros
        // index) est exécuté hors du thread UI pour ne pas bloquer la saisie. La continuation
        // reprend sur le thread UI (contexte capturé), donc SwapResults reste thread-safe.
        var queryText = SearchText;
        List<SearchResult> searchResults;
        try
        {
            searchResults = await Task.Run(() => _search.Search(queryText, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Une frappe plus récente a invalidé cette recherche → ignorer les résultats périmés
        if (token.IsCancellationRequested || generation != _searchGeneration)
            return;

        tempResults.AddRange(searchResults);
        SwapResults(tempResults);
    }
    
    /// <summary>
    /// Remplace le contenu de Results en une seule notification CollectionChanged.Reset.
    /// Élimine le flash UI causé par Clear() + N × Add() (N+1 notifications individuelles).
    /// </summary>
    private void SwapResults(List<SearchResult> newResults)
    {
        Results.ReplaceAll(newResults);
        FinalizeResults();
        
        // Charger les icônes natives en arrière-plan
        if (newResults.Count > 0)
            _ = _search.LoadIconsAsync(newResults, _searchCts?.Token ?? CancellationToken.None);
    }
    
    private void ShowRecentHistory()
    {
        Results.Clear();
        
        // Afficher d'abord les items épinglés
        foreach (var pinned in _actions.GetPinnedResults())
            Results.Add(pinned);
        
        // Puis l'historique si activé (items récemment utilisés)
        if (_settings.Search.EnableSearchHistory && _settings.Search.SearchHistory.Count > 0)
        {
            var maxHistory = Math.Max(0, 5 - _actions.PinnedCount);
            foreach (var historyItem in _settings.Search.SearchHistory.Take(maxHistory))
                Results.Add(historyItem.ToSearchResult());
        }
        
        FinalizeResults();
        
        // Charger les icônes natives en arrière-plan
        if (Results.Count > 0)
            _ = _search.LoadIconsAsync(Results.ToList(), _searchCts?.Token ?? CancellationToken.None);
    }
    
    /// <summary>
    /// Réordonne un item épinglé par drag &amp; drop et rafraîchit l'affichage.
    /// </summary>
    public void ReorderPinnedItem(int fromIndex, int toIndex)
    {
        _actions.ReorderPinned(fromIndex, toIndex);
        
        // Rafraîchir l'affichage si on est dans la vue des épingles
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            Results.Clear();
            ShowRecentHistory();
        }
    }
    
    /// <summary>
    /// Nombre d'items épinglés actuels.
    /// </summary>
    public int PinnedItemCount => _actions.PinnedCount;
    
    /// <summary>
    /// Vérifie si un résultat à l'index donné est un item épinglé.
    /// </summary>
    public bool IsResultPinned(int resultIndex)
    {
        return _actions.IsResultPinned(resultIndex, Results.Count)
               && Results[resultIndex].Description == "⭐ Épinglé";
    }
    
    /// <summary>
    /// Exécute une action applicative retournée par le SystemControlExecutor.
    /// </summary>
    private void HandleAppAction(AppAction action)
    {
        switch (action)
        {
            case AppAction.OpenSettings:
                RequestHide?.Invoke(this, EventArgs.Empty);
                RequestOpenSettings?.Invoke(this, EventArgs.Empty);
                break;
            case AppAction.Quit:
                RequestQuit?.Invoke(this, EventArgs.Empty);
                break;
            case AppAction.Reindex:
                RequestHide?.Invoke(this, EventArgs.Empty);
                RequestReindex?.Invoke(this, EventArgs.Empty);
                break;
            case AppAction.ShowHistory:
                ShowSearchHistory();
                break;
            case AppAction.ClearHistory:
                ClearHistory();
                break;
            case AppAction.ShowHelp:
                ShowHelpCommands();
                break;
        }
    }
    
    /// <summary>
    /// Dispatche une commande asynchrone vers un handler spécialisé.
    /// Gère l'indicateur de chargement et l'affichage des résultats.
    /// Remplace les anciens blocs PerformXxxAsync monolithiques.
    /// </summary>
    private async Task DispatchCommandAsync(ICommandHandler handler, string query, int generation, CancellationToken token)
    {
        IsSearching = true;
        
        try
        {
            // Indicateur de chargement générique
            Results.Clear();
            Results.Add(new SearchResult
            {
                Name = "Chargement...",
                Description = "Requête en cours...",
                Type = ResultType.SystemControl,
                DisplayIcon = "⏳"
            });
            FinalizeResults();
            
            var result = await handler.ExecuteAsync(query, token);
            
            // Vérifier que cette recherche est toujours la plus récente
            if (token.IsCancellationRequested || generation != _searchGeneration) return;
            
            Results.ReplaceAll(result.Results);
            FinalizeResults();
            
            // Charger les icônes natives en arrière-plan
            if (result.Results.Count > 0)
                _ = _search.LoadIconsAsync(result.Results, token);
        }
        catch (OperationCanceledException)
        {
            // Recherche annulée par une nouvelle requête
        }
        catch (Exception ex)
        {
            Results.Clear();
            Results.Add(new SearchResult
            {
                Name = "Erreur",
                Description = ex.Message,
                Type = ResultType.SystemControl,
                DisplayIcon = "⚠️"
            });
            FinalizeResults();
        }
        finally
        {
            IsSearching = false;
        }
    }
    
    private void FinalizeResults()
    {
        HasResults = Results.Count > 0;

        var newIndex = HasResults ? 0 : -1;
        var indexUnchanged = SelectedIndex == newIndex;
        SelectedIndex = newIndex;

        // Réaffecter la même valeur n'émet aucune notification : sans ce rappel explicite,
        // la preview et le panneau d'actions resteraient sur l'item de la recherche précédente.
        if (indexUnchanged)
            RefreshSelectionDependents(newIndex);

        UpdateGhostSuggestion();
    }
    
    /// <summary>
    /// Met à jour la suggestion fantôme via le GhostSuggestionService.
    /// </summary>
    private void UpdateGhostSuggestion()
    {
        GhostSuggestionText = _search.GetGhostSuggestion(SearchText, Results);
    }

    /// <summary>
    /// Accepte la suggestion fantôme : remplace le texte de recherche par la suggestion complète.
    /// </summary>
    /// <returns>true si une suggestion a été acceptée, false sinon.</returns>
    public bool AcceptGhostSuggestion()
    {
        if (string.IsNullOrEmpty(GhostSuggestionText))
            return false;
        
        SearchText = GhostSuggestionText;
        GhostSuggestionText = string.Empty;
        RequestCaretAtEnd?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // Le suffixe Async est retiré par le générateur : la commande reste ExecuteCommand.
    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count)
            return;

        var item = Results[SelectedIndex];

        switch (item.Type)
        {
            case ResultType.SystemCommand:
            case ResultType.SystemControl:
            case ResultType.AppControl:
                // Les items SystemControl issus de l'index (ms-settings:, control|, .msc, etc.)
                // doivent être lancés directement via LaunchService, pas via l'executor de commandes.
                // Seules les commandes préfixées par ':' sont gérées par ExecuteSystemControlAsync.
                if (!string.IsNullOrEmpty(item.Path) && !item.Path.StartsWith(':'))
                    LaunchItem(item);
                else
                    await ExecuteSystemControlAsync(item.Path);
                break;
                
            case ResultType.Note:
                // Les notes sont maintenant des widgets, pas d'action spéciale ici
                break;
                
            case ResultType.SearchHistory:
                // L'historique contient maintenant des items cliqués, on les relance
                LaunchItem(item);
                break;
                
            default:
                LaunchItem(item);
                break;
        }
    }
    
    /// <summary>
    /// Exécute l'action sélectionnée sur le résultat courant.
    /// </summary>
    [RelayCommand]
    private void ExecuteAction()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return;
        if (SelectedActionIndex < 0 || SelectedActionIndex >= AvailableActions.Count) return;
        
        var result = Results[SelectedIndex];
        var action = AvailableActions[SelectedActionIndex];
        
        ExecuteActionOnResult(action, result);
    }
    
    /// <summary>
    /// Enregistre un alias via le ResultActionService.
    /// </summary>
    public void SaveAlias(string alias, string targetPath)
    {
        var notification = _actions.SaveAlias(alias, targetPath);
        ShowNotification?.Invoke(this, notification);
    }
    
    /// <summary>
    /// Supprime l'alias associé à un chemin cible.
    /// </summary>
    public void DeleteAlias(string targetPath)
    {
        var notification = _actions.DeleteAlias(targetPath);
        if (notification != null)
            ShowNotification?.Invoke(this, notification);
    }
    
    /// <summary>
    /// Vérifie si un chemin cible possède un alias.
    /// </summary>
    public bool HasAlias(string targetPath) => _actions.HasAlias(targetPath);
    
    /// <summary>
    /// Exécute une action spécifique sur un résultat.
    /// Point unique de passage pour le panneau d'actions, le menu contextuel
    /// et les raccourcis clavier (Point #1 : centralisation).
    /// </summary>
    public void ExecuteActionOnResult(FileAction action, SearchResult result)
    {
        var outcome = _actions.ExecuteAction(action, result, string.IsNullOrWhiteSpace(SearchText));
        ApplyActionOutcome(outcome, result);
    }
    
    /// <summary>
    /// Exécute une action par type sur le résultat actuellement sélectionné.
    /// Utilisé par les raccourcis clavier du code-behind pour centraliser
    /// toute l'exécution dans le ViewModel (Point #1).
    /// </summary>
    /// <returns>true si l'action a été exécutée, false sinon.</returns>
    public bool ExecuteShortcutAction(FileActionType actionType)
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count)
            return false;
        
        var result = Results[SelectedIndex];
        var action = new FileAction { ActionType = actionType };
        var outcome = _actions.ExecuteAction(action, result, string.IsNullOrWhiteSpace(SearchText));
        ApplyActionOutcome(outcome, result);
        return true;
    }
    
    /// <summary>
    /// Interprète un <see cref="ActionOutcome"/> et applique les effets côté UI.
    /// Seul point de traduction entre le résultat déclaratif du service
    /// et les événements/mutations du ViewModel.
    /// </summary>
    private void ApplyActionOutcome(ActionOutcome outcome, SearchResult result)
    {
        // Demandes de dialogues UI (la View écoute ces events)
        if (outcome.RenameRequestPath != null)
        {
            RequestRename?.Invoke(this, outcome.RenameRequestPath);
            return;
        }
        
        if (outcome.CreateAliasRequest != null)
        {
            RequestCreateAlias?.Invoke(this, outcome.CreateAliasRequest.Value);
            return;
        }
        
        if (outcome.DeleteConfirmationRequest != null)
        {
            RequestDeleteConfirmation?.Invoke(this, outcome.DeleteConfirmationRequest);
            return;
        }
        
        // Notification
        if (outcome.Notification != null)
            ShowNotification?.Invoke(this, outcome.Notification);
        
        // Enregistrer l'usage dans l'index
        if (outcome.RecordUsage)
            _indexingService.RecordUsage(result);
        
        // Rafraîchir les actions disponibles
        if (outcome.RefreshActions)
            UpdateAvailableActions(result);
        
        // Rafraîchir les résultats (ex: après unpin/suppression)
        if (outcome.RefreshResults)
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                Results.Clear();
                ShowRecentHistory();
            }
            else
            {
                ForceRefresh();
            }
        }
        
        // Fermer le panneau d'actions
        if (outcome.CloseActionsPanel)
            ShowActionsPanel = false;
        
        // Masquer le launcher
        if (outcome.ShouldHide)
            RequestHide?.Invoke(this, EventArgs.Empty);
    }
    
    /// <summary>
    /// Exécute en mode administrateur.
    /// </summary>
    [RelayCommand]
    private void ExecuteAsAdmin()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return;
        
        var result = Results[SelectedIndex];
        if (result.Type is ResultType.Application or ResultType.Script or ResultType.File)
        {
            if (!_actions.RunAsAdmin(result))
            {
                ShowNotification?.Invoke(this, $"❌ Impossible de lancer « {result.Name} » en administrateur");
                return;
            }

            _indexingService.RecordUsage(result);
            RequestHide?.Invoke(this, EventArgs.Empty);
        }
    }
    
    /// <summary>
    /// Bascule l'affichage du panneau de prévisualisation.
    /// </summary>
    [RelayCommand]
    private void TogglePreview()
    {
        ShowPreviewPanel = !ShowPreviewPanel;
        _settingsProvider.Update(s => s.Appearance.ShowPreviewPanel = ShowPreviewPanel);
        
        if (ShowPreviewPanel && SelectedIndex >= 0 && SelectedIndex < Results.Count)
        {
            CancelPendingPreview();
            _previewCts = new CancellationTokenSource();
            _ = UpdatePreviewAsync(Results[SelectedIndex], _previewCts.Token);
        }
    }
    
    /// <summary>
    /// Bascule l'affichage du panneau d'actions.
    /// </summary>
    [RelayCommand]
    private void ToggleActions()
    {
        ShowActionsPanel = !ShowActionsPanel;
        
        if (ShowActionsPanel && SelectedIndex >= 0 && SelectedIndex < Results.Count)
        {
            UpdateAvailableActions(Results[SelectedIndex]);
        }
    }
    
    private void LaunchItem(SearchResult item)
    {
        // Lancer AVANT d'enregistrer : un raccourci cassé ou une cible déplacée
        // ne doit ni polluer l'historique, ni gonfler le compteur d'usage.
        if (!_actions.Launch(item))
        {
            // Garder la fenêtre ouverte : le toast s'affiche dessus, et l'utilisateur
            // peut corriger sa recherche au lieu de se demander pourquoi rien ne s'est passé.
            ShowNotification?.Invoke(this, $"❌ Impossible de lancer « {item.Name} »");
            return;
        }

        // Enregistrer l'item lancé dans l'historique via clone-swap (Point #2)
        if (_settings.Search.EnableSearchHistory && !string.IsNullOrWhiteSpace(item.Path))
        {
            var historyItem = new HistoryItem
            {
                Name = item.Name,
                Path = item.Path,
                Type = item.Type,
                Icon = item.DisplayIcon,
                LastUsed = DateTime.Now
            };
            _settingsProvider.Update(s => s.Search.AddToSearchHistory(historyItem));
        }

        _indexingService.RecordUsage(item);
        RequestHide?.Invoke(this, EventArgs.Empty);
    }
    

    private async Task ExecuteSystemControlAsync(string? command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        // L'await capture le contexte UI : tout ce qui suit (Results, events)
        // s'exécute bien sur le thread UI.
        var executionResult = await _actions.ExecuteSystemControlAsync(command);
        
        if (!executionResult.Handled)
        {
            // Commande async non gérée par l'executor → re-router via CommandRouter
            ReRouteAsyncCommand(command);
            return;
        }
        
        // Appliquer le résultat déclaratif
        // Actions applicatives
        if (executionResult.AppAction != null)
        {
            HandleAppAction(executionResult.AppAction.Value);
            return;
        }
        
        if (executionResult.AutoCompleteText != null)
        {
            SearchText = executionResult.AutoCompleteText;
            RequestCaretAtEnd?.Invoke(this, EventArgs.Empty);
            return;
        }
        
        if (executionResult.ResultsToShow != null)
        {
            Results.Clear();
            foreach (var r in executionResult.ResultsToShow)
                Results.Add(r);
            FinalizeResults();
        }
        
        if (executionResult.Notification != null)
            ShowNotification?.Invoke(this, executionResult.Notification);
        
        if (executionResult.ScreenCaptureMode != null)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                RequestScreenCapture?.Invoke(this, executionResult.ScreenCaptureMode));
            return;
        }
        
        if (executionResult.ShouldHide)
            RequestHide?.Invoke(this, EventArgs.Empty);
    }
    
    /// <summary>
    /// Re-route une commande async (météo, traduction, IA) vers le CommandRouter
    /// quand le SystemControlExecutor ne la gère pas (exécution via Enter sur un résultat).
    /// </summary>
    private void ReRouteAsyncCommand(string command)
    {
        var parts = command.TrimStart(':').Split(' ', 2);
        var cmdPrefix = parts[0];
        var arg = parts.Length > 1 ? parts[1] : null;
        
        var matchedCmd = _settings.SystemCommands.FirstOrDefault(c =>
            c.IsEnabled && c.Prefix.Equals(cmdPrefix, StringComparison.OrdinalIgnoreCase));
        
        if (matchedCmd == null) return;
        
        var fullQuery = matchedCmd.FullPrefix + (arg != null ? $" {arg}" : "");
        var handler = _search.FindCommandHandler(fullQuery);
        if (handler != null)
            _ = DispatchCommandAsync(handler, fullQuery, _searchGeneration, _searchCts?.Token ?? CancellationToken.None);
    }
    
    
    private void ShowSearchHistory()
    {
        Results.Clear();
        
        if (_settings.Search.SearchHistory.Count == 0)
        {
            Results.Add(new SearchResult
            {
                Name = "Aucun historique",
                Description = "Votre historique est vide",
                Type = ResultType.SystemCommand,
                DisplayIcon = "📭"
            });
        }
        else
        {
            foreach (var historyItem in _settings.Search.SearchHistory)
            {
                Results.Add(historyItem.ToSearchResult());
            }
        }
        
        FinalizeResults();
    }
    
    private void ClearHistory()
    {
        _settingsProvider.Update(s => s.Search.ClearSearchHistory());
        SearchText = string.Empty;
        RequestHide?.Invoke(this, EventArgs.Empty);
    }
    
    private void ShowHelpCommands()
    {
        var helpResults = _search.BuildHelpResults();
        Results.ReplaceAll(helpResults);
        FinalizeResults();
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        
        var newIndex = SelectedIndex + delta;
        
        if (newIndex < 0) 
            newIndex = Results.Count - 1;
        else if (newIndex >= Results.Count) 
            newIndex = 0;
        
        SelectedIndex = newIndex;
    }
    
    public void MoveActionSelection(int delta)
    {
        if (AvailableActions.Count == 0) return;
        
        var newIndex = SelectedActionIndex + delta;
        
        if (newIndex < 0) 
            newIndex = AvailableActions.Count - 1;
        else if (newIndex >= AvailableActions.Count) 
            newIndex = 0;
        
        SelectedActionIndex = newIndex;
    }
    
    /// <summary>
    /// Recharge les settings depuis le fichier pour synchroniser les changements
    /// (notamment les épingles modifiées depuis d'autres contextes).
    /// </summary>
    public void ReloadSettings()
    {
        _settingsProvider.Reload();
        ShowCategoryBadges = _settings.Appearance.ShowCategoryBadges;
        ShowSettingsButton = _settings.Appearance.ShowSettingsButton;
        ShowShortcutHints = _settings.Appearance.ShowShortcutHints;
    }
    
    public void Reset()
    {
        SearchText = string.Empty;
        GhostSuggestionText = string.Empty;
        Results.Clear();
        SelectedIndex = -1;
        HasResults = false;
        CurrentPreview = null;
        ShowActionsPanel = false;
        AvailableActions.Clear();
        
        // Forcer l'affichage des épingles et historique
        // (OnSearchTextChanged ne se déclenche pas si SearchText était déjà vide)
        ShowRecentHistory();
    }
    
    /// <summary>
    /// Force le rafraîchissement des résultats de recherche.
    /// Annule tout debounce en cours et exécute la recherche immédiatement.
    /// </summary>
    public void ForceRefresh()
    {
        _debounceCts?.Cancel();
        _ = UpdateResultsInternalAsync();
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        CancelPendingPreview();
        _fileWatcherService?.Dispose();
        _search?.Dispose();
        
        GC.SuppressFinalize(this);
    }
}


