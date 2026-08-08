namespace QuickLauncher;

/// <summary>
/// Constantes centralisées de l'application.
/// </summary>
public static class Constants
{
    public const string AppName = "QuickLauncher";
    public const string Version = "1.1.0";
    
    /// <summary>Repo GitHub servant de source pour les mises à jour automatiques (Velopack).</summary>
    public const string UpdateRepositoryUrl = "https://github.com/1231016Felix/lapriselemay_personalRepo";
    
    // Chemins relatifs
    public const string SettingsFileName = "settings.json";
    public const string DatabaseFileName = "index.db";
    public const string LogFileName = "app.log";
    public const string IndexingLogFileName = "indexing.log";
    
    // Registre Windows
    public const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    
    // Hotkey
    public const int HotkeyId = 9000;
    public const int WM_HOTKEY = 0x0312;
    
    // Limites
    public const int DefaultMaxResults = 8;
    public const int DefaultSearchDepth = 5;
    public const int DefaultMaxSearchHistory = 10;
    public const int MaxScoreBonus = 50;
    
    // Timeouts
    public const int IndexingBatchSize = 100;
    public const int SearchDebounceMs = 50;
    
    // AI Debounce
    public const int AiDebounceSecondsDefault = 3;
    public const int AiDebounceSecondsMin = 1;
    public const int AiDebounceSecondsMax = 4;
    
    // Scores de recherche
    public static class SearchScores
    {
        public const int ExactMatch = 150;
        public const int StartsWith = 100;
        public const int Contains = 50;
        public const int InitialsMatch = 30;
        public const int FuzzyMatch = 20;
        public const int ApplicationBonus = 20;
        public const int ScriptBonus = 15;
        public const int FolderBonus = 10;
        public const int UsageMultiplier = 5;
    }
    
    // Extensions par défaut
    public static readonly string[] DefaultFileExtensions =
    [
        ".exe", ".lnk", ".bat", ".cmd", ".ps1", ".msi",
        ".txt", ".pdf", ".docx", ".xlsx", ".pptx",
        ".png", ".jpg", ".jpeg", ".gif", ".mp3", ".mp4"
    ];
    
    // Couleurs
    public static class Colors
    {
        public const string DefaultAccent = "#0078D4";
        public const string Green = "#107C10";
        public const string Red = "#E81123";
        public const string Orange = "#FF8C00";
        public const string Purple = "#881798";
        public const string Pink = "#E3008C";
        public const string Turquoise = "#00B7C3";
    }
}
