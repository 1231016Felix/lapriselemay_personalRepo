using QuickLauncher.Models;

namespace QuickLauncher.Services.CommandHandlers;

/// <summary>
/// Actions applicatives que le ViewModel doit exécuter.
/// </summary>
public enum AppAction
{
    OpenSettings,
    Quit,
    Reindex,
    ShowHistory,
    ClearHistory,
    ShowHelp
}

/// <summary>
/// Résultat de l'exécution d'une commande de contrôle système.
/// </summary>
public sealed class SystemControlExecutionResult
{
    /// <summary>Si true, la fenêtre doit être cachée après exécution.</summary>
    public bool ShouldHide { get; init; }
    
    /// <summary>Notification à afficher à l'utilisateur (null = aucune).</summary>
    public string? Notification { get; init; }
    
    /// <summary>Si non-null, le texte de recherche doit être remplacé par cette valeur (autocomplete).</summary>
    public string? AutoCompleteText { get; init; }
    
    /// <summary>Si non-null, une capture d'écran a été demandée avec ce mode.</summary>
    public string? ScreenCaptureMode { get; init; }
    
    /// <summary>Résultats à afficher dans la liste (null = pas de changement).</summary>
    public List<SearchResult>? ResultsToShow { get; init; }
    
    /// <summary>Si non-null, une action applicative doit être exécutée par le ViewModel.</summary>
    public AppAction? AppAction { get; init; }
    
    /// <summary>Si true, la commande a été gérée par cet exécuteur.</summary>
    public bool Handled { get; init; }
    
    /// <summary>Shortcut pour un résultat non géré.</summary>
    public static readonly SystemControlExecutionResult NotHandled = new() { Handled = false };
}

/// <summary>
/// Exécute les commandes de contrôle système (volume, lock, timer, note, screenshot, etc.).
/// Extrait de LauncherViewModel.ExecuteSystemControlAsync() pour découpler la logique métier
/// de la couche présentation.
/// 
/// Contrairement aux ICommandHandler qui gèrent la phase de *recherche* (affichage des résultats),
/// cet exécuteur gère la phase d'*exécution* (quand l'utilisateur appuie sur Entrée).
/// </summary>
public interface ISystemControlExecutor
{
    /// <summary>
    /// Tente d'exécuter une commande de contrôle système.
    /// Async : certaines commandes (wifi) attendent des API WinRT et ne doivent
    /// pas bloquer le thread UI ; les autres s'exécutent de manière synchrone.
    /// </summary>
    /// <param name="command">Commande brute (ex: ":volume 50", ":lock", ":translate:copy:texte").</param>
    /// <returns>Résultat de l'exécution.</returns>
    Task<SystemControlExecutionResult> ExecuteAsync(string command);
}
