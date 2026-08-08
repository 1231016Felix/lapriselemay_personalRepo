namespace QuickLauncher.Models;

/// <summary>
/// Item de l'historique de recherche (dernier lancement).
/// </summary>
public sealed class HistoryItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public ResultType Type { get; set; }
    public string? Icon { get; set; }
    public DateTime LastUsed { get; set; }
    
    public SearchResult ToSearchResult() => new()
    {
        Name = Name, Path = Path, Type = Type,
        Description = $"🕐 {LastUsed:dd/MM HH:mm}",
        // Source unique : évite que l'historique dérive du jeu d'icônes principal
        // (cette liste dupliquait celle de SearchResult, en moins complète).
        DisplayIcon = Icon ?? SearchResult.GetDefaultIcon(Type)
    };
}