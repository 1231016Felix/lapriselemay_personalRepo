using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Résultat renvoyé par <see cref="ResultActionService.Execute"/>.
/// Décrit de manière déclarative ce que l'appelant (ViewModel) doit faire côté UI.
/// Aucune logique UI ici — le ViewModel interprète l'outcome et délègue à la View si nécessaire.
/// </summary>
public sealed class ActionOutcome
{
    /// <summary>Notification à afficher (null = aucune).</summary>
    public string? Notification { get; init; }
    
    /// <summary>Masquer la fenêtre du launcher après l'action.</summary>
    public bool ShouldHide { get; init; }
    
    /// <summary>Enregistrer l'usage dans l'index (RecordUsage).</summary>
    public bool RecordUsage { get; init; }
    
    /// <summary>Rafraîchir la liste des actions disponibles.</summary>
    public bool RefreshActions { get; init; }
    
    /// <summary>Fermer le panneau d'actions.</summary>
    public bool CloseActionsPanel { get; init; }
    
    /// <summary>Rafraîchir les résultats (ex: après unpin quand SearchText est vide).</summary>
    public bool RefreshResults { get; init; }
    
    /// <summary>Demander à la View d'ouvrir le dialogue de renommage.</summary>
    public string? RenameRequestPath { get; init; }
    
    /// <summary>Demander à la View d'ouvrir le dialogue de création d'alias.</summary>
    public (string Name, string Path)? CreateAliasRequest { get; init; }
    
    /// <summary>Demander à la View de confirmer la suppression.</summary>
    public SearchResult? DeleteConfirmationRequest { get; init; }
    
    /// <summary>Outcome vide (aucune action supplémentaire).</summary>
    public static ActionOutcome None { get; } = new();
}

/// <summary>
/// Service centralisé d'exécution des actions sur les résultats de recherche.
/// Point unique de passage pour toutes les actions (panneau latéral, menu contextuel, raccourcis clavier).
/// Élimine la duplication entre LauncherViewModel.ExecuteActionOnResult() et LauncherWindow.ExecuteContextAction().
/// Extrait de LauncherViewModel (Points #1 et #2).
/// </summary>
public sealed class ResultActionService
{
    private readonly ISettingsProvider _settingsProvider;
    private readonly AliasService _aliasService;
    private readonly IndexingService _indexingService;
    private readonly PinnedItemsManager _pinnedItemsManager;
    private readonly NotesService _notesService;
    private readonly IFileActionExecutor _fileActionExecutor;
    private readonly ILaunchService _launchService;

    public ResultActionService(
        ISettingsProvider settingsProvider,
        AliasService aliasService,
        IndexingService indexingService,
        PinnedItemsManager pinnedItemsManager,
        NotesService notesService,
        IFileActionExecutor fileActionExecutor,
        ILaunchService launchService)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _aliasService = aliasService ?? throw new ArgumentNullException(nameof(aliasService));
        _indexingService = indexingService ?? throw new ArgumentNullException(nameof(indexingService));
        _pinnedItemsManager = pinnedItemsManager ?? throw new ArgumentNullException(nameof(pinnedItemsManager));
        _notesService = notesService ?? throw new ArgumentNullException(nameof(notesService));
        _fileActionExecutor = fileActionExecutor ?? throw new ArgumentNullException(nameof(fileActionExecutor));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
    }

    /// <summary>
    /// Exécute une action sur un résultat de recherche.
    /// Retourne un <see cref="ActionOutcome"/> déclaratif que le ViewModel interprète.
    /// </summary>
    /// <param name="action">L'action à exécuter.</param>
    /// <param name="result">Le résultat de recherche cible.</param>
    /// <param name="isSearchEmpty">True si le champ de recherche est vide (vue épingles).</param>
    public ActionOutcome Execute(FileAction action, SearchResult result, bool isSearchEmpty)
    {
        return action.ActionType switch
        {
            // === Actions déléguées à la View (nécessitent un dialogue UI) ===
            FileActionType.Rename => new ActionOutcome { RenameRequestPath = result.Path },
            
            FileActionType.CreateAlias => new ActionOutcome
            {
                CreateAliasRequest = (result.Name, result.Path)
            },
            
            FileActionType.Delete => ExecuteDelete(result),
            
            // === Actions gérées en interne ===
            FileActionType.DeleteAlias => ExecuteDeleteAlias(result),
            FileActionType.Pin => ExecutePin(result),
            FileActionType.Unpin => ExecuteUnpin(result, isSearchEmpty),
            
            // === RunAsAdmin : déléguer à LaunchService qui résout les .lnk et StoreApps ===
            FileActionType.RunAsAdmin => ExecuteRunAsAdmin(result),
            
            // === Actions fichier standard ===
            _ => ExecuteFileAction(action, result)
        };
    }
    
    /// <summary>
    /// Vérifie si un chemin cible possède un alias.
    /// </summary>
    public bool HasAlias(string targetPath) => _aliasService.GetAliasByTargetPath(targetPath) != null;
    
    /// <summary>
    /// Enregistre un alias via l'AliasService.
    /// </summary>
    /// <returns>Message de notification.</returns>
    public string SaveAlias(string alias, string targetPath)
    {
        _aliasService.SetAlias(alias, targetPath);
        return $"⌨️ Alias '{alias}' créé";
    }
    
    /// <summary>
    /// Supprime l'alias associé à un chemin cible.
    /// </summary>
    /// <returns>Message de notification, ou null si aucun alias trouvé.</returns>
    public string? DeleteAlias(string targetPath)
    {
        var entry = _aliasService.GetAliasByTargetPath(targetPath);
        if (entry == null) return null;
        
        _aliasService.RemoveAlias(entry.Alias);
        return $"⌨️ Alias '{entry.Alias}' supprimé";
    }

    private ActionOutcome ExecuteDelete(SearchResult result)
    {
        // Suppression directe pour les notes (pas de confirmation nécessaire)
        if (result.Type == ResultType.Note && result.Path.StartsWith(":note:id:"))
        {
            if (int.TryParse(result.Path[9..], out var noteId))
            {
                _notesService.DeleteNote(noteId);
                return new ActionOutcome
                {
                    Notification = "🗑️ Note supprimée",
                    RefreshResults = true,
                    CloseActionsPanel = true
                };
            }
            return ActionOutcome.None;
        }
        
        // Fichiers/dossiers → demander confirmation à la View
        return new ActionOutcome { DeleteConfirmationRequest = result };
    }

    private ActionOutcome ExecuteDeleteAlias(SearchResult result)
    {
        var notification = DeleteAlias(result.Path);
        return new ActionOutcome
        {
            Notification = notification,
            RefreshActions = true,
            CloseActionsPanel = true
        };
    }

    private ActionOutcome ExecutePin(SearchResult result)
    {
        var notification = _pinnedItemsManager.Pin(result.Name, result.Path, result.Type, result.DisplayIcon);
        return new ActionOutcome
        {
            Notification = notification,
            RefreshActions = true,
            CloseActionsPanel = true
        };
    }

    /// <summary>
    /// Exécute l'Unpin complet : mutation settings via clone-swap + outcome déclaratif.
    /// </summary>
    private ActionOutcome ExecuteUnpin(SearchResult result, bool isSearchEmpty)
    {
        _pinnedItemsManager.Unpin(result.Path);
        return new ActionOutcome
        {
            Notification = "📌 Désépinglé",
            RefreshActions = true,
            CloseActionsPanel = true,
            RefreshResults = isSearchEmpty
        };
    }

    /// <summary>
    /// Exécute en admin via LaunchService qui résout correctement les raccourcis .lnk
    /// et les AppUserModelId (StoreApps), contrairement à FileActionExecutor qui lance le path brut.
    /// </summary>
    private ActionOutcome ExecuteRunAsAdmin(SearchResult result)
    {
        if (!_launchService.RunAsAdmin(result))
        {
            // Échec : garder la fenêtre ouverte pour que le toast soit visible,
            // et ne pas comptabiliser un usage qui n'a pas eu lieu.
            return new ActionOutcome
            {
                Notification = $"❌ Impossible de lancer « {result.Name} » en administrateur",
                CloseActionsPanel = true
            };
        }

        return new ActionOutcome
        {
            ShouldHide = true,
            RecordUsage = true,
            CloseActionsPanel = true
        };
    }

    private ActionOutcome ExecuteFileAction(FileAction action, SearchResult result)
    {
        // Pour CopyName, passer le nom d'affichage plutôt que le path
        // (important pour les StoreApps où Path = package family name)
        var targetPath = action.ActionType == FileActionType.CopyName
            ? result.Name
            : result.Path;
        
        var success = _fileActionExecutor.Execute(action.ActionType, targetPath);
        
        if (!success)
        {
            // Notification d'échec spécifique
            if (action.ActionType == FileActionType.OpenInVSCode)
                return new ActionOutcome { Notification = "❌ VS Code introuvable" };
            
            return ActionOutcome.None;
        }
        
        // Notification de succès selon l'action
        var notification = action.ActionType switch
        {
            FileActionType.CopyUrl => "🔗 URL copiée",
            FileActionType.CopyPath => "📋 Chemin copié",
            FileActionType.CopyName => "📋 Nom copié",
            FileActionType.Compress => "🗜️ Archive ZIP créée",
            FileActionType.SendByEmail => "📧 Email en cours...",
            _ => (string?)null
        };
        
        // Les actions qui ouvrent quelque chose doivent masquer le launcher
        var shouldHide = action.ActionType is FileActionType.Open
            or FileActionType.RunAsAdmin
            or FileActionType.OpenPrivate
            or FileActionType.OpenWith
            or FileActionType.OpenLocation
            or FileActionType.OpenInTerminal
            or FileActionType.OpenInExplorer
            or FileActionType.OpenInVSCode
            or FileActionType.EditInEditor
            or FileActionType.SendByEmail;
        
        return new ActionOutcome
        {
            Notification = notification,
            ShouldHide = shouldHide,
            RecordUsage = shouldHide,
            CloseActionsPanel = true
        };
    }
}
