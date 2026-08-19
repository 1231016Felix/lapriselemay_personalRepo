using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Abstraction pour le fournisseur d'actions sur les résultats de recherche.
/// Permet l'injection de dépendances et la testabilité.
/// Extrait de FileAction.cs (Amélioration #1 : séparation des responsabilités).
/// </summary>
public interface IFileActionProvider
{
    List<FileAction> GetActionsForResult(SearchResult result);
    List<FileAction> GetActionsForResult(SearchResult result, bool isPinned, bool hasAlias = false);
}

/// <summary>
/// Fournit les actions disponibles selon le type de résultat.
/// Les icônes sont des glyphes Segoe MDL2 Assets (rendus vectoriels, teintés par
/// le thème) — même langage d'icônes que la fenêtre principale du launcher.
/// </summary>
public class FileActionProvider : IFileActionProvider
{
    public List<FileAction> GetActionsForResult(SearchResult result)
    {
        return GetActionsForResult(result, isPinned: false, hasAlias: false);
    }
    
    public List<FileAction> GetActionsForResult(SearchResult result, bool isPinned, bool hasAlias = false)
    {
        var actions = new List<FileAction>();
        
        actions.Add(new FileAction
        {
            Name = "Ouvrir",
            Icon = "\uE8E5",
            Description = "Ouvrir l'élément",
            Shortcut = "Entrée",
            ActionType = FileActionType.Open
        });
        
        switch (result.Type)
        {
            case ResultType.Application: actions.AddRange(GetApplicationActions()); break;
            case ResultType.StoreApp: actions.AddRange(GetStoreAppActions()); break;
            case ResultType.File: actions.AddRange(GetFileActions()); break;
            case ResultType.Script: actions.AddRange(GetScriptActions()); break;
            case ResultType.Folder: actions.AddRange(GetFolderActions()); break;
            case ResultType.Bookmark: actions.AddRange(GetBookmarkActions()); break;
            case ResultType.WebSearch: actions.AddRange(GetWebSearchActions()); break;
            case ResultType.Calculator: actions.AddRange(GetCalculatorActions()); break;
        }
        
        if (result.Type is not (ResultType.WebSearch or ResultType.Calculator 
            or ResultType.SystemCommand or ResultType.SystemControl or ResultType.SearchHistory))
        {
            actions.AddRange(GetAliasActions(hasAlias));
            actions.AddRange(GetPinActions(isPinned));
        }
        
        return actions;
    }
    
    private static IEnumerable<FileAction> GetApplicationActions()
    {
        yield return new FileAction { Name = "Exécuter en admin", Icon = "\uE7EF", Description = "Exécuter avec les droits administrateur", Shortcut = "Ctrl+Entrée", ActionType = FileActionType.RunAsAdmin };
        yield return new FileAction { Name = "Ouvrir l'emplacement", Icon = "\uE838", Description = "Ouvrir le dossier contenant le fichier", Shortcut = "Ctrl+O", ActionType = FileActionType.OpenLocation };
        yield return new FileAction { Name = "Copier le chemin", Icon = "\uE8C8", Description = "Copier le chemin complet dans le presse-papiers", Shortcut = "Ctrl+Maj+C", ActionType = FileActionType.CopyPath };
    }
    
    private static IEnumerable<FileAction> GetStoreAppActions() => [];
    
    private static IEnumerable<FileAction> GetFileActions()
    {
        yield return new FileAction { Name = "Ouvrir avec...", Icon = "\uE7AC", Description = "Choisir l'application pour ouvrir le fichier", ActionType = FileActionType.OpenWith };
        yield return new FileAction { Name = "Ouvrir l'emplacement", Icon = "\uE838", Description = "Ouvrir le dossier contenant le fichier", Shortcut = "Ctrl+O", ActionType = FileActionType.OpenLocation };
        yield return new FileAction { Name = "Copier le chemin", Icon = "\uE8C8", Description = "Copier le chemin complet dans le presse-papiers", Shortcut = "Ctrl+Maj+C", ActionType = FileActionType.CopyPath };
        yield return new FileAction { Name = "Copier le nom", Icon = "\uE8C8", Description = "Copier le nom du fichier", ActionType = FileActionType.CopyName };
        yield return new FileAction { Name = "Compresser (ZIP)", Icon = "\uE7B8", Description = "Créer une archive ZIP du fichier", ActionType = FileActionType.Compress };
        yield return new FileAction { Name = "Envoyer par email", Icon = "\uE715", Description = "Envoyer le fichier en pièce jointe", ActionType = FileActionType.SendByEmail };
        yield return new FileAction { Name = "Renommer", Icon = "\uE8AC", Description = "Renommer le fichier", Shortcut = "F2", ActionType = FileActionType.Rename };
        yield return new FileAction { Name = "Supprimer", Icon = "\uE74D", Description = "Envoyer à la corbeille", Shortcut = "Suppr", ActionType = FileActionType.Delete, RequiresConfirmation = true };
        yield return new FileAction { Name = "Propriétés", Icon = "\uE946", Description = "Afficher les propriétés du fichier", ActionType = FileActionType.Properties };
    }
    
    private static IEnumerable<FileAction> GetScriptActions()
    {
        yield return new FileAction { Name = "Exécuter en admin", Icon = "\uE7EF", Description = "Exécuter avec les droits administrateur", Shortcut = "Ctrl+Entrée", ActionType = FileActionType.RunAsAdmin };
        yield return new FileAction { Name = "Éditer", Icon = "\uE70F", Description = "Ouvrir dans l'éditeur de texte par défaut", ActionType = FileActionType.EditInEditor };
        yield return new FileAction { Name = "Ouvrir l'emplacement", Icon = "\uE838", Description = "Ouvrir le dossier contenant le script", Shortcut = "Ctrl+O", ActionType = FileActionType.OpenLocation };
        yield return new FileAction { Name = "Copier le chemin", Icon = "\uE8C8", Description = "Copier le chemin complet dans le presse-papiers", Shortcut = "Ctrl+Maj+C", ActionType = FileActionType.CopyPath };
    }
    
    private static IEnumerable<FileAction> GetFolderActions()
    {
        yield return new FileAction { Name = "Ouvrir dans l'Explorateur", Icon = "\uE838", Description = "Ouvrir le dossier dans l'Explorateur Windows", ActionType = FileActionType.OpenInExplorer };
        yield return new FileAction { Name = "Ouvrir dans le Terminal", Icon = "\uE756", Description = "Ouvrir un terminal dans ce dossier", Shortcut = "Ctrl+T", ActionType = FileActionType.OpenInTerminal };
        yield return new FileAction { Name = "Ouvrir dans VS Code", Icon = "\uE943", Description = "Ouvrir le dossier dans Visual Studio Code", ActionType = FileActionType.OpenInVSCode };
        yield return new FileAction { Name = "Copier le chemin", Icon = "\uE8C8", Description = "Copier le chemin complet dans le presse-papiers", Shortcut = "Ctrl+Maj+C", ActionType = FileActionType.CopyPath };
        yield return new FileAction { Name = "Compresser (ZIP)", Icon = "\uE7B8", Description = "Créer une archive ZIP du dossier", ActionType = FileActionType.Compress };
        yield return new FileAction { Name = "Renommer", Icon = "\uE8AC", Description = "Renommer le dossier", Shortcut = "F2", ActionType = FileActionType.Rename };
        yield return new FileAction { Name = "Propriétés", Icon = "\uE946", Description = "Afficher les propriétés du dossier", ActionType = FileActionType.Properties };
    }
    
    private static IEnumerable<FileAction> GetBookmarkActions()
    {
        yield return new FileAction { Name = "Ouvrir en privé", Icon = "\uE72E", Description = "Ouvrir en navigation privée", Shortcut = "Ctrl+Maj+Entrée", ActionType = FileActionType.OpenPrivate };
        yield return new FileAction { Name = "Copier l'URL", Icon = "\uE71B", Description = "Copier l'adresse dans le presse-papiers", Shortcut = "Ctrl+C", ActionType = FileActionType.CopyUrl };
    }
    
    private static IEnumerable<FileAction> GetWebSearchActions()
    {
        yield return new FileAction { Name = "Ouvrir en privé", Icon = "\uE72E", Description = "Rechercher en navigation privée", ActionType = FileActionType.OpenPrivate };
        yield return new FileAction { Name = "Copier l'URL", Icon = "\uE71B", Description = "Copier le lien de recherche", ActionType = FileActionType.CopyUrl };
    }
    
    private static IEnumerable<FileAction> GetCalculatorActions()
    {
        yield return new FileAction { Name = "Copier le résultat", Icon = "\uE8C8", Description = "Copier le résultat dans le presse-papiers", ActionType = FileActionType.CopyUrl };
    }
    
    private static IEnumerable<FileAction> GetAliasActions(bool hasAlias)
    {
        if (hasAlias)
            yield return new FileAction { Name = "Supprimer l'alias", Icon = "\uE765", Description = "Retirer le raccourci texte de cet élément", ActionType = FileActionType.DeleteAlias };
        else
            yield return new FileAction { Name = "Créer un alias", Icon = "\uE765", Description = "Assigner un raccourci texte à cet élément", ActionType = FileActionType.CreateAlias };
    }
    
    private static IEnumerable<FileAction> GetPinActions(bool isPinned)
    {
        if (isPinned)
            yield return new FileAction { Name = "Désépingler", Icon = "\uE77A", Description = "Retirer des favoris épinglés", ActionType = FileActionType.Unpin };
        else
            yield return new FileAction { Name = "Épingler", Icon = "\uE718", Description = "Ajouter aux favoris épinglés", ActionType = FileActionType.Pin };
    }
}
