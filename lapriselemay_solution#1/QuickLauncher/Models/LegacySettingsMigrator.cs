using System.Text.Json;
using QuickLauncher.Models.Settings;

namespace QuickLauncher.Models;

/// <summary>
/// Migre l'ancien format JSON plat vers le nouveau format sectionné (v2).
/// Classe isolée : à supprimer une fois que tous les utilisateurs ont migré.
/// </summary>
internal static class LegacySettingsMigrator
{
    /// <summary>
    /// Désérialise un JSON au format legacy plat et retourne un AppSettings v2.
    /// Sauvegarde immédiatement au nouveau format pour ne migrer qu'une seule fois.
    /// </summary>
    public static AppSettings Migrate(string json, JsonSerializerOptions jsonOptions)
    {
        var legacy = JsonSerializer.Deserialize<LegacyAppSettings>(json, jsonOptions);
        if (legacy == null) return new AppSettings();
        
        var settings = new AppSettings();
        
        // Search
        settings.Search.MaxResults = legacy.MaxResults;
        settings.Search.SearchDepth = legacy.SearchDepth;
        settings.Search.IndexHiddenFolders = legacy.IndexHiddenFolders;
        settings.Search.IndexBrowserBookmarks = legacy.IndexBrowserBookmarks;
        settings.Search.EnableAliases = legacy.EnableAliases;
        settings.Search.SystemSearchDepth = legacy.SystemSearchDepth;
        settings.Search.EnableSearchHistory = legacy.EnableSearchHistory;
        settings.Search.MaxSearchHistory = legacy.MaxSearchHistory;
        settings.Search.SearchHistory = legacy.SearchHistory ?? [];
        settings.Search.ScoringWeights = legacy.ScoringWeights ?? new ScoringWeights();
        settings.Search.IndexedFolders = legacy.IndexedFolders ?? settings.Search.IndexedFolders;
        settings.Search.FileExtensions = legacy.FileExtensions ?? settings.Search.FileExtensions;
        settings.Search.PinnedItems = legacy.PinnedItems ?? [];
        settings.Search.Scripts = legacy.Scripts ?? [];
        settings.Search.SearchEngines = legacy.SearchEngines ?? settings.Search.SearchEngines;        settings.Search.EnableFileWatcher = legacy.EnableFileWatcher;
        settings.Search.AutoReindexEnabled = legacy.AutoReindexEnabled;
        settings.Search.AutoReindexMode = legacy.AutoReindexMode;
        settings.Search.AutoReindexIntervalMinutes = legacy.AutoReindexIntervalMinutes;
        settings.Search.AutoReindexScheduledTime = legacy.AutoReindexScheduledTime ?? "03:00";
        
        // Appearance
        settings.Appearance.Theme = legacy.Theme ?? "Dark";
        settings.Appearance.ThemeMode = legacy.ThemeMode;
        settings.Appearance.AccentColor = legacy.AccentColor ?? Constants.Colors.DefaultAccent;
        settings.Appearance.WindowOpacity = legacy.WindowOpacity;
        settings.Appearance.WindowPosition = "Remember";
        settings.Appearance.LastWindowLeft = legacy.LastWindowLeft;
        settings.Appearance.LastWindowTop = legacy.LastWindowTop;
        settings.Appearance.ShowInTaskbar = legacy.ShowInTaskbar;
        settings.Appearance.ShowSettingsButton = legacy.ShowSettingsButton;
        settings.Appearance.ShowPreviewPanel = legacy.ShowPreviewPanel;
        settings.Appearance.ShowShortcutHints = legacy.ShowShortcutHints;
        settings.Appearance.ShowCategoryBadges = legacy.ShowCategoryBadges;
        settings.Appearance.ShowIndexingStatus = legacy.ShowIndexingStatus;
        settings.Appearance.EnableAnimations = legacy.EnableAnimations;
        settings.Appearance.AnimationDurationMs = legacy.AnimationDurationMs;
        settings.Appearance.AnimationStyle = legacy.AnimationStyle;
        settings.Appearance.StaggerDelayMs = legacy.StaggerDelayMs;
        settings.Appearance.AutoThemeLightStart = legacy.AutoThemeLightStart ?? "07:00";
        settings.Appearance.AutoThemeDarkStart = legacy.AutoThemeDarkStart ?? "19:00";
        settings.Appearance.LightThemeStartTime = legacy.LightThemeStartTime ?? "07:00";
        settings.Appearance.DarkThemeStartTime = legacy.DarkThemeStartTime ?? "19:00";
        
        // Integrations
        settings.Integrations.WeatherCity = legacy.WeatherCity ?? "Montreal";
        settings.Integrations.WeatherUnit = legacy.WeatherUnit ?? "celsius";
        settings.Integrations.TranslateTargetLang = legacy.TranslateTargetLang ?? "en";
        settings.Integrations.TranslateSourceLang = legacy.TranslateSourceLang ?? "auto";
        settings.Integrations.AiProvider = legacy.AiProvider ?? "chatgpt";
        settings.Integrations.AiApiUrl = legacy.AiApiUrl ?? "https://api.openai.com/v1/chat/completions";
        try
        {
            settings.Integrations.AiApiKeyDecrypted = legacy.AiApiKey ?? string.Empty;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // DPAPI indisponible : conserver la valeur legacy telle quelle (déjà en clair dans
            // l'ancien fichier) plutôt que de perdre l'ensemble des réglages migrés.
            settings.Integrations.AiApiKey = legacy.AiApiKey ?? string.Empty;
            Services.Log.Warning($"[Settings] Clé API non chiffrée pendant la migration : {ex.Message}");
        }
        settings.Integrations.AiModel = legacy.AiModel ?? "gpt-4o-mini";
        settings.Integrations.AiSystemPrompt = legacy.AiSystemPrompt ?? settings.Integrations.AiSystemPrompt;
        settings.Integrations.NoteWidgets = legacy.NoteWidgets ?? [];
        settings.Integrations.TimerWidgets = legacy.TimerWidgets ?? [];
        settings.Integrations.Notes = legacy.Notes ?? [];
        
        // Root
        settings.SystemCommands = legacy.SystemCommands ?? DefaultSystemCommands.Create();
        settings.Hotkey = legacy.Hotkey ?? new HotkeySettings();
        settings.StartWithWindows = legacy.StartWithWindows;
        settings.CloseAfterLaunch = legacy.CloseAfterLaunch;
        settings.MinimizeOnStartup = legacy.MinimizeOnStartup;
        settings.SingleClickLaunch = legacy.SingleClickLaunch;
        
        // Sauvegarder immédiatement au nouveau format
        settings.Save();
        System.Diagnostics.Debug.WriteLine("[Settings] Migration legacy → v2 effectuée");
        
        return settings;
    }
}

// ══════════════════════════════════════════════════════════
//  DTO LEGACY — utilisé uniquement pour la désérialisation
//  de l'ancien format JSON plat. À supprimer avec LegacySettingsMigrator.
// ══════════════════════════════════════════════════════════

internal sealed class LegacyAppSettings
{
    // Search
    public int MaxResults { get; set; } = Constants.DefaultMaxResults;
    public int SearchDepth { get; set; } = Constants.DefaultSearchDepth;
    public bool IndexHiddenFolders { get; set; }
    public bool IndexBrowserBookmarks { get; set; } = true;
    public bool EnableAliases { get; set; } = true;
    public int SystemSearchDepth { get; set; } = 5;
    public bool EnableSearchHistory { get; set; } = true;
    public int MaxSearchHistory { get; set; } = Constants.DefaultMaxSearchHistory;
    public List<HistoryItem>? SearchHistory { get; set; }
    public ScoringWeights? ScoringWeights { get; set; }
    public List<string>? IndexedFolders { get; set; }
    public List<string>? FileExtensions { get; set; }
    public List<PinnedItem>? PinnedItems { get; set; }
    public List<CustomScript>? Scripts { get; set; }
    public List<WebSearchEngine>? SearchEngines { get; set; }
    public bool EnableFileWatcher { get; set; } = true;
    public bool AutoReindexEnabled { get; set; }
    public AutoReindexMode AutoReindexMode { get; set; } = AutoReindexMode.Interval;
    public int AutoReindexIntervalMinutes { get; set; } = 60;
    public string? AutoReindexScheduledTime { get; set; }
    
    // Appearance
    public string? Theme { get; set; }
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Dark;
    public string? AccentColor { get; set; }
    public double WindowOpacity { get; set; } = 1.0;
    public string? WindowPosition { get; set; }
    public double? LastWindowLeft { get; set; }
    public double? LastWindowTop { get; set; }
    public bool ShowInTaskbar { get; set; }
    public bool ShowSettingsButton { get; set; } = true;
    public bool ShowPreviewPanel { get; set; } = true;
    public bool ShowShortcutHints { get; set; } = true;
    public bool ShowCategoryBadges { get; set; } = true;
    public bool ShowIndexingStatus { get; set; } = true;
    public bool EnableAnimations { get; set; } = true;
    public int AnimationDurationMs { get; set; } = 140;
    public AnimationStyle AnimationStyle { get; set; } = AnimationStyle.FadeSlide;
    public int StaggerDelayMs { get; set; } = 30;
    public string? AutoThemeLightStart { get; set; }
    public string? AutoThemeDarkStart { get; set; }
    public string? LightThemeStartTime { get; set; }
    public string? DarkThemeStartTime { get; set; }
    
    // Integrations
    public string? WeatherCity { get; set; }
    public string? WeatherUnit { get; set; }
    public string? TranslateTargetLang { get; set; }
    public string? TranslateSourceLang { get; set; }
    public string? AiProvider { get; set; }
    public string? AiApiUrl { get; set; }
    public string? AiApiKey { get; set; }
    public string? AiModel { get; set; }
    public string? AiSystemPrompt { get; set; }
    public List<NoteWidgetInfo>? NoteWidgets { get; set; }
    public List<TimerWidgetInfo>? TimerWidgets { get; set; }
    public List<NoteItem>? Notes { get; set; }
    
    // Root
    public List<SystemControlCommand>? SystemCommands { get; set; }
    public HotkeySettings? Hotkey { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public bool CloseAfterLaunch { get; set; } = true;
    public bool MinimizeOnStartup { get; set; } = true;
    public bool SingleClickLaunch { get; set; }
}