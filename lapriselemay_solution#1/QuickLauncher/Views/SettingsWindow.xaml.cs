using Microsoft.Win32;
using QuickLauncher.Models;
using QuickLauncher.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;

namespace QuickLauncher.Views;

public partial class SettingsWindow : Window
{
    /// <summary>
    /// Toujours le snapshot courant du provider : la fenêtre ne garde aucune copie propre.
    /// Toute modification passe par <see cref="Mutate"/> (clone-swap + sauvegarde), si bien
    /// qu'un Update concurrent (minuterie, widget, historique) ne peut plus faire éditer
    /// à la fenêtre un objet orphelin, ni lui faire sauvegarder une vieille copie.
    /// </summary>
    private AppSettings _settings => _settingsProvider.Current;
    private readonly ISettingsProvider _settingsProvider;
    private readonly IndexingService? _indexingService;
    private readonly ThemeService? _themeService;
    private readonly UniversalSearchService? _universalSearchService;
    private readonly IBookmarkService _bookmarkService;
    private readonly WebIntegrationService _webService = new();
    
    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "QuickLauncher";
    
    // Valeurs initiales du raccourci pour détecter les changements
    private bool _initialHotkeyAlt;
    private bool _initialHotkeyCtrl;
    private bool _initialHotkeyShift;
    private bool _initialHotkeyWin;
    private string _initialHotkeyKey = "Space";
    
    // Flag pour éviter les sauvegardes pendant le chargement initial
    private bool _isLoading = true;
    
    // Timer pour le feedback de sauvegarde
    private readonly DispatcherTimer _saveIndicatorTimer;

    public SettingsWindow(IndexingService? indexingService = null, ISettingsProvider? settingsProvider = null,
        ThemeService? themeService = null, UniversalSearchService? universalSearchService = null,
        IBookmarkService? bookmarkService = null)
    {
        InitializeComponent();
        
        // Version affichée dans « À propos » : lue depuis Constants.Version, que
        // release.ps1 bumpe à chaque release. Ne jamais la coder en dur en XAML.
        AboutVersionText.Text = $"Version {Constants.Version} • Par Felix-Antoine";
        
        _settingsProvider = settingsProvider ?? new SettingsProvider();
        _indexingService = indexingService;
        _themeService = themeService;
        _universalSearchService = universalSearchService;
        _bookmarkService = bookmarkService ?? new BookmarkService();
        
        // Initialiser le timer pour le feedback de sauvegarde
        _saveIndicatorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _saveIndicatorTimer.Tick += SaveIndicatorTimer_Tick;
        
        // Sauvegarder les valeurs initiales du raccourci
        _initialHotkeyAlt = _settings.Hotkey.UseAlt;
        _initialHotkeyCtrl = _settings.Hotkey.UseCtrl;
        _initialHotkeyShift = _settings.Hotkey.UseShift;
        _initialHotkeyWin = _settings.Hotkey.UseWin;
        _initialHotkeyKey = _settings.Hotkey.Key;
        
        LoadSettings();
        LoadStatistics();
        
        _isLoading = false;
    }

    private void LoadSettings()
    {
        // Général
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        MinimizeOnStartupCheck.IsChecked = _settings.MinimizeOnStartup;
        CloseAfterLaunchCheck.IsChecked = _settings.CloseAfterLaunch;
        ShowIndexingStatusCheck.IsChecked = _settings.Appearance.ShowIndexingStatus;
        ShowSettingsButtonCheck.IsChecked = _settings.Appearance.ShowSettingsButton;
        SingleClickLaunchCheck.IsChecked = _settings.SingleClickLaunch;
        MaxResultsSlider.Value = _settings.Search.MaxResults;
        MaxResultsValue.Text = _settings.Search.MaxResults.ToString();
        
        // Historique
        EnableSearchHistoryCheck.IsChecked = _settings.Search.EnableSearchHistory;
        MaxHistorySlider.Value = _settings.Search.MaxSearchHistory;
        MaxHistoryValue.Text = _settings.Search.MaxSearchHistory.ToString();
        
        // Recherche intelligente (recency)
        EnableRecencyBonusCheck.IsChecked = _settings.Search.ScoringWeights.EnableRecencyBonus;
        
        // Apparence
        SelectComboByTag(ThemeModeCombo, _settings.Appearance.Theme);
        // Afficher/masquer le panneau Auto selon le mode
        AutoThemePanel.Visibility = _settings.Appearance.Theme == "Auto" ? Visibility.Visible : Visibility.Collapsed;
        // Heures pour mode Auto
        if (!string.IsNullOrEmpty(_settings.Appearance.LightThemeStartTime))
            SelectComboByTag(LightStartCombo, _settings.Appearance.LightThemeStartTime);
        else
            LightStartCombo.SelectedIndex = 2; // 07:00 par défaut
        if (!string.IsNullOrEmpty(_settings.Appearance.DarkThemeStartTime))
            SelectComboByTag(DarkStartCombo, _settings.Appearance.DarkThemeStartTime);
        else
            DarkStartCombo.SelectedIndex = 2; // 19:00 par défaut
        // Badges de catégorie
        ShowCategoryBadgesCheck.IsChecked = _settings.Appearance.ShowCategoryBadges;
        // Suggestions fantômes
        ShowGhostSuggestionsCheck.IsChecked = _settings.Appearance.ShowGhostSuggestions;
        // Animations
        EnableAnimationsCheck.IsChecked = _settings.Appearance.EnableAnimations;
        AnimationSpeedSlider.Value = _settings.Appearance.AnimationDurationMs;
        AnimationSpeedValue.Text = $"{_settings.Appearance.AnimationDurationMs} ms";
        SelectComboByTag(AnimationStyleCombo, _settings.Appearance.AnimationStyle.ToString());
        UpdateAnimationSpeedPanelVisibility();
        OpacitySlider.Value = _settings.Appearance.WindowOpacity;
        OpacityValue.Text = $"{(int)(_settings.Appearance.WindowOpacity * 100)}%";
        SelectComboByTag(AccentColorCombo, _settings.Appearance.AccentColor);
        UpdateColorPreview(_settings.Appearance.AccentColor);
        
        // Raccourci
        HotkeyAltCheck.IsChecked = _settings.Hotkey.UseAlt;
        HotkeyCtrlCheck.IsChecked = _settings.Hotkey.UseCtrl;
        HotkeyShiftCheck.IsChecked = _settings.Hotkey.UseShift;
        HotkeyWinCheck.IsChecked = _settings.Hotkey.UseWin;
        SelectComboByTag(HotkeyKeyCombo, _settings.Hotkey.Key);
        UpdateHotkeyDisplay();
        
        // Indexation
        IndexedFoldersList.ItemsSource = _settings.Search.IndexedFolders;
        SearchDepthSlider.Value = _settings.Search.SearchDepth;
        SearchDepthValue.Text = _settings.Search.SearchDepth.ToString();
        SearchDepthHint.Text = GetSearchDepthHint(_settings.Search.SearchDepth);
        IndexHiddenFoldersCheck.IsChecked = _settings.Search.IndexHiddenFolders;
        
        // Recherche système
        SystemSearchDepthSlider.Value = _settings.Search.SystemSearchDepth;
        SystemSearchDepthValue.Text = _settings.Search.SystemSearchDepth.ToString();
        SystemSearchDepthHint.Text = GetSystemSearchDepthHint(_settings.Search.SystemSearchDepth);
        LoadSearchEngineInfo(forceRefresh: true);
        
        // Charger la liste des navigateurs
        LoadBrowsersList();
        
        // Réindexation automatique
        AutoReindexEnabledCheck.IsChecked = _settings.Search.AutoReindexEnabled;
        ReindexIntervalRadio.IsChecked = _settings.Search.AutoReindexMode == AutoReindexMode.Interval;
        ReindexTimeRadio.IsChecked = _settings.Search.AutoReindexMode == AutoReindexMode.ScheduledTime;
        SelectComboByTag(ReindexIntervalCombo, _settings.Search.AutoReindexIntervalMinutes.ToString());
        LoadScheduledTime(_settings.Search.AutoReindexScheduledTime);
        UpdateAutoReindexOptionsVisibility();
        
        // Recherche Web
        SearchEnginesList.ItemsSource = _settings.Search.SearchEngines;
        
        // Commandes système
        LoadSystemCommands();
        
        // Intégrations web
        WeatherCityBox.Text = _settings.Integrations.WeatherCity;
        SelectComboByTag(WeatherUnitCombo, _settings.Integrations.WeatherUnit);
        
        // Assistant IA
        SelectComboByTag(AiProviderCombo, _settings.Integrations.AiProvider);
        AiApiUrlBox.Text = _settings.Integrations.AiApiUrl;
        AiApiKeyBox.Password = _settings.Integrations.AiApiKeyDecrypted;
        AiModelBox.Text = _settings.Integrations.AiModel;
        AiSystemPromptBox.Text = _settings.Integrations.AiSystemPrompt;
        AiDebounceSlider.Value = Math.Clamp(_settings.Integrations.AiDebounceSeconds, Constants.AiDebounceSecondsMin, Constants.AiDebounceSecondsMax);
        AiDebounceValue.Text = $"{(int)AiDebounceSlider.Value}s";
        UpdateAiFieldsState();
        
        // À propos
        DataPathText.Text = AppSettings.GetSettingsPath();
    }
    
    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }
    
    private void LoadScheduledTime(string time)
    {
        var parts = time.Split(':');
        if (parts.Length == 2)
        {
            SelectComboByTag(ReindexHourCombo, parts[0]);
            SelectComboByTag(ReindexMinuteCombo, parts[1]);
        }
        else
        {
            ReindexHourCombo.SelectedIndex = 3;
            ReindexMinuteCombo.SelectedIndex = 0;
        }
    }
    
    private void UpdateColorPreview(string color)
    {
        try
        {
            ColorPreview.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Couleur invalide '{color}': {ex.Message}");
        }
    }
    
    private void UpdateAutoReindexOptionsVisibility()
    {
        var enabled = AutoReindexEnabledCheck.IsChecked == true;
        AutoReindexOptionsPanel.IsEnabled = enabled;
        AutoReindexOptionsPanel.Opacity = enabled ? 1.0 : 0.5;
    }
    
    private void LoadStatistics()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dbPath = Path.Combine(appData, "QuickLauncher", "index.db");
            
            var stats = new List<string>();
            
            if (File.Exists(dbPath))
            {
                var fileInfo = new FileInfo(dbPath);
                stats.Add($"📦 Taille de l'index: {fileInfo.Length / 1024.0:F1} Ko");
                stats.Add($"📅 Dernière indexation: {fileInfo.LastWriteTime:g}");
            }
            
            if (_indexingService != null)
                stats.Add($"🔢 Éléments indexés: {_indexingService.IndexedItemsCount}");
            
            stats.Add($"📂 Dossiers surveillés: {_settings.Search.IndexedFolders.Count}");
            stats.Add($"📄 Extensions indexées: {_settings.Search.FileExtensions.Count}");
            stats.Add($"🔍 Moteurs de recherche: {_settings.Search.SearchEngines.Count}");
            stats.Add($"🕐 Historique: {_settings.Search.SearchHistory.Count} entrées");
            
            StatsText.Text = string.Join("\n", stats);
        }
        catch
        {
            StatsText.Text = "Impossible de charger les statistiques.";
        }
    }

    // === Gestionnaires d'événements - Sliders ===
    
    private void MaxResultsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MaxResultsValue != null)
            MaxResultsValue.Text = ((int)e.NewValue).ToString();
        
        if (!_isLoading)
        {
            Mutate(s => s.Search.MaxResults = (int)e.NewValue);
        }
    }
    
    private void MaxHistorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MaxHistoryValue != null)
            MaxHistoryValue.Text = ((int)e.NewValue).ToString();
        
        if (!_isLoading)
        {
            Mutate(s => s.Search.MaxSearchHistory = (int)e.NewValue);
        }
    }
    
    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue != null)
            OpacityValue.Text = $"{(int)(e.NewValue * 100)}%";
        
        if (!_isLoading)
        {
            Mutate(s => s.Appearance.WindowOpacity = e.NewValue);
        }
    }
    
    private static string GetSearchDepthHint(int depth) => depth switch
    {
        1 => "Très limité — seuls les fichiers à la racine des dossiers indexés.",
        2 => "Minimal — raccourcis du Bureau et du menu Démarrer, peu de sous-dossiers.",
        3 => "Léger — couvre la plupart des raccourcis, rapide à indexer.",
        4 or 5 => "Équilibré — bon compromis entre couverture et performance.",
        6 or 7 => "Approfondi — explore plus de sous-dossiers, indexation un peu plus longue.",
        8 or 9 => "Très profond — couverture quasi totale, peut ralentir l'indexation.",
        10 => "Maximum — explore tout, indexation la plus longue et la plus gourmande en mémoire.",
        _ => ""
    };
    
    private static string GetSystemSearchDepthHint(int depth) => depth switch
    {
        2 => "Très rapide — ne cherche qu'aux premiers niveaux, résultats limités.",
        3 => "Rapide — bon pour trouver des fichiers proches de la racine.",
        4 or 5 => "Équilibré — bonne couverture avec un temps de réponse raisonnable.",
        6 or 7 => "Approfondi — trouve des fichiers plus enfouis, réponse un peu plus lente.",
        8 or 9 => "Très profond — recherche exhaustive, peut prendre quelques secondes.",
        10 => "Maximum — parcourt tout l'arbre de fichiers, risque de lenteur sur de gros disques.",
        _ => ""
    };
    
    private void SearchDepthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SearchDepthValue != null)
            SearchDepthValue.Text = ((int)e.NewValue).ToString();
        
        if (SearchDepthHint != null)
            SearchDepthHint.Text = GetSearchDepthHint((int)e.NewValue);
        
        if (!_isLoading)
        {
            Mutate(s => s.Search.SearchDepth = (int)e.NewValue);
        }
    }
    
    private void SystemSearchDepthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SystemSearchDepthValue != null)
            SystemSearchDepthValue.Text = ((int)e.NewValue).ToString();
        
        if (SystemSearchDepthHint != null)
            SystemSearchDepthHint.Text = GetSystemSearchDepthHint((int)e.NewValue);
        
        if (!_isLoading)
        {
            Mutate(s => s.Search.SystemSearchDepth = (int)e.NewValue);
        }
    }
    
    private void AnimationSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AnimationSpeedValue != null)
            AnimationSpeedValue.Text = $"{(int)e.NewValue} ms";
        
        if (!_isLoading)
        {
            Mutate(s => s.Appearance.AnimationDurationMs = (int)e.NewValue);
        }
    }
    
    private void AnimationStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading && AnimationStyleCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            if (Enum.TryParse<AnimationStyle>(tag, out var style))
            {
                Mutate(s => s.Appearance.AnimationStyle = style);
            }
        }
    }
    
    private void UpdateAnimationSpeedPanelVisibility()
    {
        if (AnimationSpeedPanel != null)
        {
            var enabled = EnableAnimationsCheck.IsChecked == true;
            AnimationSpeedPanel.IsEnabled = enabled;
            AnimationSpeedPanel.Opacity = enabled ? 1.0 : 0.4;
        }
    }
    
    private void LoadSearchEngineInfo(bool forceRefresh = false)
    {
        var info = _universalSearchService?.GetEngineInfo(forceRefresh) ?? new SearchEngineInfo
        {
            Status = SearchEngineStatus.DirectSearch, Name = "Non disponible",
            Description = "Service non initialisé", IsOptimal = false, Icon = "❓"
        };
        
        SearchEngineIcon.Text = info.Icon;
        SearchEngineName.Text = info.Name;
        SearchEngineDescription.Text = info.Description;
        
        // Style selon le statut. Texte noir sur les deux badges : SuccessBrush et
        // WarningBrush sont des teintes claires, même règle de luminance que les
        // badges de catégorie du launcher (le blanc sur orange était à 1.9:1).
        if (info.IsOptimal)
        {
            SearchEngineIndicator.Background = ThemeBrush("SuccessSurfaceBrush");
            SearchEngineStatusBadge.Background = ThemeBrush("SuccessBrush");
            SearchEngineStatusText.Text = "✓ Optimal";
            SearchEngineStatusText.Foreground = new SolidColorBrush(Colors.Black);
        }
        else
        {
            SearchEngineIndicator.Background = ThemeBrush("WarningSurfaceBrush");
            SearchEngineStatusBadge.Background = ThemeBrush("WarningBrush");
            SearchEngineStatusText.Text = "⚠ Non optimal";
            SearchEngineStatusText.Foreground = new SolidColorBrush(Colors.Black);
        }
        
        // Recommandation
        if (!string.IsNullOrEmpty(info.Recommendation))
        {
            SearchEngineRecommendation.Visibility = Visibility.Visible;
            SearchEngineRecommendationText.Text = info.Recommendation;
        }
        else
        {
            SearchEngineRecommendation.Visibility = Visibility.Collapsed;
        }
        
        // Avertissement spécifique à Windows Search
        WindowsSearchWarning.Visibility = info.Status == SearchEngineStatus.WindowsSearch
            ? Visibility.Visible
            : Visibility.Collapsed;
        
        // Avertissement spécifique à la recherche directe
        if (info.Status == SearchEngineStatus.DirectSearch)
        {
            DirectSearchWarning.Visibility = Visibility.Visible;
            var folders = (_universalSearchService?.GetDefaultSearchPaths() ?? [])
                .Where(System.IO.Directory.Exists)
                .Select(p => "• " + p);
            DirectSearchFoldersList.Text = string.Join("\n", folders);
        }
        else
        {
            DirectSearchWarning.Visibility = Visibility.Collapsed;
        }
        
        // Stats du cache
        UpdateSearchCacheStats();
    }
    
    private void UpdateSearchCacheStats()
    {
        var (entryCount, totalResults) = _universalSearchService?.GetCacheStats() ?? (0, 0);
        if (entryCount > 0)
            SearchCacheStats.Text = $"Cache: {entryCount} recherches, {totalResults} résultats";
        else
            SearchCacheStats.Text = "Cache vide";
    }
    
    private void ClearSearchCache_Click(object sender, RoutedEventArgs e)
    {
        _universalSearchService?.ClearCache();
        UpdateSearchCacheStats();
        MessageBox.Show("Cache de recherche vidé!", "Succès", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenWindowsIndexingSettings_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "control.exe",
                Arguments = "srchadmin.dll",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Impossible d'ouvrir les paramètres d'indexation: {ex.Message}");
            MessageBox.Show("Impossible d'ouvrir les paramètres d'indexation Windows.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // === Gestionnaires d'événements - Apparence ===
    
    private void ThemeModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading && ThemeModeCombo.SelectedItem is ComboBoxItem { Tag: string theme })
        {
            // Muter d'abord : ThemeService lit le snapshot courant (horaires du mode Auto)
            Mutate(s => s.Appearance.Theme = theme);
            AutoThemePanel.Visibility = theme == "Auto" ? Visibility.Visible : Visibility.Collapsed;

            // Appliquer le thème immédiatement (gère Dark, Light, Auto, System)
            _themeService?.ApplyTheme(theme);
        }
    }
    
    private void AutoThemeTime_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading)
        {
            var lightStart = (LightStartCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var darkStart = (DarkStartCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            Mutate(s =>
            {
                if (lightStart != null) s.Appearance.AutoThemeLightStart = lightStart;
                if (darkStart != null) s.Appearance.AutoThemeDarkStart = darkStart;
            });

            // Réappliquer le thème si en mode Auto (après la mutation : le service lit le snapshot courant)
            if (_settings.Appearance.Theme == "Auto")
            {
                ApplyAutoTheme();
            }
        }
    }
    
    private void ApplyAutoTheme()
    {
        _themeService?.ApplyTheme("Auto");
    }
    
    private void AccentColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccentColorCombo.SelectedItem is ComboBoxItem { Tag: string color })
        {
            UpdateColorPreview(color);
            if (!_isLoading)
            {
                Mutate(s => s.Appearance.AccentColor = color);
                // Appliquer immédiatement, comme le fait ThemeModeCombo_SelectionChanged
                // pour le thème. Sans cet appel la couleur était bien persistée mais
                // n'atterrissait dans les ressources qu'au prochain démarrage (App.OnStartup)
                // ou au détour d'un changement de thème : titres de section, valeurs
                // accentuées et badges restaient sur l'ancienne couleur.
                _themeService?.ApplyAccentColor(color);
            }
        }
    }

    // === Gestionnaires d'événements - Raccourci ===
    
    private void Hotkey_Changed(object sender, RoutedEventArgs e)
    {
        UpdateHotkeyDisplay();
        if (!_isLoading)
        {
            SaveHotkeySettings();
            CheckHotkeyChanged();
        }
    }
    
    private void HotkeyKeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateHotkeyDisplay();
        if (!_isLoading)
        {
            SaveHotkeySettings();
            CheckHotkeyChanged();
        }
    }
    
    private void SaveHotkeySettings()
    {
        var useAlt = HotkeyAltCheck.IsChecked == true;
        var useCtrl = HotkeyCtrlCheck.IsChecked == true;
        var useShift = HotkeyShiftCheck.IsChecked == true;
        var useWin = HotkeyWinCheck.IsChecked == true;
        var key = GetComboTag(HotkeyKeyCombo) ?? "Space";
        Mutate(s =>
        {
            s.Hotkey.UseAlt = useAlt;
            s.Hotkey.UseCtrl = useCtrl;
            s.Hotkey.UseShift = useShift;
            s.Hotkey.UseWin = useWin;
            s.Hotkey.Key = key;
        });
    }
    
    private void CheckHotkeyChanged()
    {
        bool hasChanged = 
            _settings.Hotkey.UseAlt != _initialHotkeyAlt ||
            _settings.Hotkey.UseCtrl != _initialHotkeyCtrl ||
            _settings.Hotkey.UseShift != _initialHotkeyShift ||
            _settings.Hotkey.UseWin != _initialHotkeyWin ||
            _settings.Hotkey.Key != _initialHotkeyKey;
        
        RestartWarningPanel.Visibility = hasChanged ? Visibility.Visible : Visibility.Collapsed;
    }
    
    private void RestartApp_Click(object sender, RoutedEventArgs e)
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            Process.Start(exePath);
            Application.Current.Shutdown();
        }
    }
    
    private void UpdateHotkeyDisplay()
    {
        if (CurrentHotkeyDisplay == null) return;
        
        var parts = new List<string>();
        if (HotkeyCtrlCheck?.IsChecked == true) parts.Add("Ctrl");
        if (HotkeyAltCheck?.IsChecked == true) parts.Add("Alt");
        if (HotkeyShiftCheck?.IsChecked == true) parts.Add("Shift");
        if (HotkeyWinCheck?.IsChecked == true) parts.Add("Win");
        
        var key = (HotkeyKeyCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Space";
        parts.Add(key);
        
        CurrentHotkeyDisplay.Text = string.Join(" + ", parts);
    }

    // === Gestionnaires d'événements - Dossiers ===
    
    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Sélectionner un dossier à indexer",
            UseDescriptionForTitle = true
        };
        
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var selectedPath = dialog.SelectedPath;
            if (!_settings.Search.IndexedFolders.Contains(selectedPath, StringComparer.OrdinalIgnoreCase))
            {
                Mutate(s => s.Search.IndexedFolders.Add(selectedPath));
                RefreshFoldersList();
            }
            else
            {
                MessageBox.Show("Ce dossier est déjà dans la liste.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IndexedFoldersList.SelectedItem is string folder)
        {
            if (_settings.Search.IndexedFolders.Count > 1)
            {
                Mutate(s => s.Search.IndexedFolders.Remove(folder));
                RefreshFoldersList();
            }
            else
            {
                MessageBox.Show("Vous devez conserver au moins un dossier indexé.", "Attention",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
    
    private void ResetFolders_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Réinitialiser les dossiers indexés aux valeurs par défaut ?",
            "Réinitialiser",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes)
        {
            Mutate(s => s.Search.ResetIndexedFolders());
            RefreshFoldersList();
        }
    }
    
    private void RefreshFoldersList()
    {
        IndexedFoldersList.ItemsSource = null;
        IndexedFoldersList.ItemsSource = _settings.Search.IndexedFolders;
    }
    
    // === Sauvegarde automatique ===
    
    /// <summary>
    /// Applique une modification via le provider (clone du snapshot courant, mutation,
    /// sauvegarde, publication atomique) puis affiche le résultat RÉEL de l'écriture.
    /// Ne jamais muter <see cref="_settings"/> directement : c'est un snapshot partagé
    /// avec les recherches en arrière-plan, et il peut être remplacé à tout moment.
    /// </summary>
    private void Mutate(Action<AppSettings> change)
    {
        if (_isLoading) return;

        var saved = _settingsProvider.Update(change);
        Debug.WriteLine(saved ? "[Settings] Auto-sauvegardé" : "[Settings] ÉCHEC de la sauvegarde");

        // Afficher le feedback visuel
        ShowSaveIndicator(saved);
    }
    
    /// <summary>
    /// Résout une brush du thème par sa clé (Styles.xaml).
    /// Centralise les couleurs d'état : un seul vert/orange/rouge dans toute
    /// l'application, au lieu des Color.FromRgb locaux qui divergeaient entre onglets.
    /// </summary>
    private Brush ThemeBrush(string key) => (Brush)FindResource(key);

    private void ShowSaveIndicator(bool saved = true)
    {
        if (saved)
        {
            AutoSaveIndicator.Text = "✓ Sauvegardé";
            AutoSaveIndicator.Foreground = ThemeBrush("SuccessBrush");
            _saveIndicatorTimer.Stop();
            _saveIndicatorTimer.Start();
        }
        else
        {
            // L'échec reste affiché (pas de minuterie de retour) : il doit être vu.
            _saveIndicatorTimer.Stop();
            AutoSaveIndicator.Text = "⚠ Échec de la sauvegarde (voir le journal)";
            AutoSaveIndicator.Foreground = ThemeBrush("DangerBrush");
        }
    }

    private void SaveIndicatorTimer_Tick(object? sender, EventArgs e)
    {
        _saveIndicatorTimer.Stop();
        AutoSaveIndicator.Text = "💾 Sauvegarde automatique";
        AutoSaveIndicator.Foreground = ThemeBrush("TextTertiaryBrush");
    }
    
    // === Gestionnaires génériques pour CheckBox ===
    
    private void CheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        
        // Lire toutes les cases sur le thread UI, puis les appliquer au clone en une seule fois
        var startWithWindows = StartWithWindowsCheck.IsChecked == true;
        var minimizeOnStartup = MinimizeOnStartupCheck.IsChecked == true;
        var closeAfterLaunch = CloseAfterLaunchCheck.IsChecked == true;
        var showIndexingStatus = ShowIndexingStatusCheck.IsChecked == true;
        var showSettingsButton = ShowSettingsButtonCheck.IsChecked == true;
        var singleClickLaunch = SingleClickLaunchCheck.IsChecked == true;
        var enableSearchHistory = EnableSearchHistoryCheck.IsChecked == true;
        var indexHiddenFolders = IndexHiddenFoldersCheck.IsChecked == true;
        var showCategoryBadges = ShowCategoryBadgesCheck.IsChecked == true;
        var showGhostSuggestions = ShowGhostSuggestionsCheck.IsChecked == true;
        var enableAnimations = EnableAnimationsCheck.IsChecked == true;
        var enableRecencyBonus = EnableRecencyBonusCheck.IsChecked == true;

        Mutate(s =>
        {
            s.StartWithWindows = startWithWindows;
            s.MinimizeOnStartup = minimizeOnStartup;
            s.CloseAfterLaunch = closeAfterLaunch;
            s.Appearance.ShowIndexingStatus = showIndexingStatus;
            s.Appearance.ShowSettingsButton = showSettingsButton;
            s.SingleClickLaunch = singleClickLaunch;
            s.Search.EnableSearchHistory = enableSearchHistory;
            s.Search.IndexHiddenFolders = indexHiddenFolders;
            s.Appearance.ShowCategoryBadges = showCategoryBadges;
            s.Appearance.ShowGhostSuggestions = showGhostSuggestions;
            s.Appearance.EnableAnimations = enableAnimations;
            s.Search.ScoringWeights.EnableRecencyBonus = enableRecencyBonus;
        });

        UpdateAnimationSpeedPanelVisibility();
        UpdateStartupRegistry();
    }

    // === Gestionnaires d'événements - Intégrations web ===
    
    private void WeatherCityBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Validation automatique à la perte de focus seulement si la valeur a changé
        var city = WeatherCityBox.Text.Trim();
        if (!_isLoading && !string.IsNullOrEmpty(city) && city != _settings.Integrations.WeatherCity)
        {
            _ = ValidateAndSaveWeatherCityAsync(city);
        }
    }
    
    private void WeatherCityBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            var city = WeatherCityBox.Text.Trim();
            if (!string.IsNullOrEmpty(city))
            {
                _ = ValidateAndSaveWeatherCityAsync(city);
                e.Handled = true;
            }
        }
    }
    
    private void WeatherValidateBtn_Click(object sender, RoutedEventArgs e)
    {
        var city = WeatherCityBox.Text.Trim();
        if (!string.IsNullOrEmpty(city))
        {
            _ = ValidateAndSaveWeatherCityAsync(city);
        }
    }
    
    private async Task ValidateAndSaveWeatherCityAsync(string city)
    {
        // Feedback: en cours
        WeatherValidationPanel.Visibility = Visibility.Visible;
        WeatherValidationPanel.Background = ThemeBrush("WarningSurfaceBrush");
        WeatherValidationText.Foreground = ThemeBrush("WarningBrush");
        WeatherValidationText.Text = $"🔍 Vérification de « {city} »...";
        WeatherValidateBtn.IsEnabled = false;
        WeatherCityBox.IsEnabled = false;
        
        try
        {
            var result = await _webService.ValidateCityAsync(city);
            
            if (result.HasValue)
            {
                var (resolvedCity, country) = result.Value;
                var display = country != null ? $"{resolvedCity}, {country}" : resolvedCity;
                
                // Utiliser le nom résolu par l'API
                WeatherCityBox.Text = resolvedCity;
                Mutate(s => s.Integrations.WeatherCity = resolvedCity);
                
                // Feedback: succès
                WeatherValidationPanel.Background = ThemeBrush("SuccessSurfaceBrush");
                WeatherValidationText.Foreground = ThemeBrush("SuccessBrush");
                WeatherValidationText.Text = $"✅ Ville trouvée : {display}";
            }
            else
            {
                // Feedback: échec — ne pas sauvegarder
                WeatherCityBox.Text = _settings.Integrations.WeatherCity;
                WeatherValidationPanel.Background = ThemeBrush("DangerSurfaceBrush");
                WeatherValidationText.Foreground = ThemeBrush("DangerBrush");
                WeatherValidationText.Text = $"❌ Ville « {city} » introuvable. Valeur restaurée.";
            }
        }
        catch
        {
            // Erreur réseau — ne pas sauvegarder
            WeatherCityBox.Text = _settings.Integrations.WeatherCity;
            WeatherValidationPanel.Background = ThemeBrush("WarningSurfaceBrush");
            WeatherValidationText.Foreground = ThemeBrush("WarningBrush");
            WeatherValidationText.Text = "⚠️ Impossible de valider (pas de connexion). Valeur restaurée.";
        }
        finally
        {
            WeatherValidateBtn.IsEnabled = true;
            WeatherCityBox.IsEnabled = true;
        }
    }
    
    private void WeatherUnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading && WeatherUnitCombo.SelectedItem is ComboBoxItem { Tag: string unit })
        {
            Mutate(s => s.Integrations.WeatherUnit = unit);
        }
    }
    

    // === Gestionnaires d'événements - Assistant IA ===
    
    private void AiProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || AiProviderCombo.SelectedItem is not ComboBoxItem selected) return;
        
        var provider = selected.Tag?.ToString() ?? "chatgpt";
        
        switch (provider)
        {
            case "chatgpt":
                AiApiUrlBox.Text = "https://api.openai.com/v1/chat/completions";
                AiModelBox.Text = "gpt-4o-mini";
                break;
            case "gemini":
                AiApiUrlBox.Text = "https://generativelanguage.googleapis.com/v1beta";
                AiModelBox.Text = "gemini-2.0-flash";
                break;
            case "ollama":
                AiApiUrlBox.Text = "http://localhost:11434/v1/chat/completions";
                AiModelBox.Text = "llama3.2";
                break;
            // "custom" = on ne touche pas aux champs
        }
        
        var apiUrl = AiApiUrlBox.Text;
        var model = AiModelBox.Text;
        Mutate(s =>
        {
            s.Integrations.AiProvider = provider;
            s.Integrations.AiApiUrl = apiUrl;
            s.Integrations.AiModel = model;
        });
        UpdateAiFieldsState();
    }
    
    /// <summary>
    /// Met à jour l'état des champs IA selon le fournisseur sélectionné.
    /// </summary>
    private void UpdateAiFieldsState()
    {
        if (AiProviderCombo?.SelectedItem is not ComboBoxItem selected) return;
        var provider = selected.Tag?.ToString() ?? "chatgpt";
        
        // URL et modèle en lecture seule pour les presets, éditable pour custom
        var isCustom = provider == "custom";
        AiApiUrlBox.IsReadOnly = !isCustom;
        AiApiUrlBox.Opacity = isCustom ? 1.0 : 0.7;
        
        // Hint pour la clé API
        if (AiApiKeyHint != null)
        {
            AiApiKeyHint.Text = provider switch
            {
                "ollama" => "(optionnel)",
                "chatgpt" => "(requis)",
                "gemini" => "(requis — Google AI Studio)",
                _ => "(selon le fournisseur)"
            };
        }
    }
    
    private void AiApiUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            var apiUrl = AiApiUrlBox.Text.Trim();
            Mutate(s => s.Integrations.AiApiUrl = apiUrl);
        }
    }
    
    private void AiApiKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            var apiKey = AiApiKeyBox.Password;
            try
            {
                Mutate(s => s.Integrations.AiApiKeyDecrypted = apiKey);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // DPAPI a échoué : la clé protégée précédente est conservée telle quelle,
                // jamais remplacée par une valeur en clair (cf. SecureStorageService.Encrypt).
                ShowSaveIndicator(false);
                MessageBox.Show(
                    "Impossible de protéger la clé API avec le chiffrement Windows (DPAPI).\n\n" +
                    "La nouvelle clé n'a pas été enregistrée ; la précédente est conservée.",
                    "Clé API non enregistrée", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
    
    private void AiModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            var model = AiModelBox.Text.Trim();
            Mutate(s => s.Integrations.AiModel = model);
        }
    }
    
    private void AiSystemPromptBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            var prompt = AiSystemPromptBox.Text;
            Mutate(s => s.Integrations.AiSystemPrompt = prompt);
        }
    }
    
    private void AiDebounceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || AiDebounceValue == null) return;
        var seconds = (int)e.NewValue;
        AiDebounceValue.Text = $"{seconds}s";
        Mutate(s => s.Integrations.AiDebounceSeconds = seconds);
    }
    
    private async void AiTestBtn_Click(object sender, RoutedEventArgs e)
    {
        AiTestBtn.IsEnabled = false;
        AiTestResultPanel.Visibility = Visibility.Visible;
        AiTestResultPanel.Background = ThemeBrush("WarningSurfaceBrush");
        AiTestResultText.Foreground = ThemeBrush("WarningBrush");
        AiTestResultText.Text = "🔄 Test de connexion en cours...";
        
        try
        {
            using var aiService = new AiChatService();
            var providerTag = (AiProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "chatgpt";
            var (success, message) = await aiService.TestConnectionAsync(
                AiApiUrlBox.Text.Trim(),
                AiApiKeyBox.Password,
                AiModelBox.Text.Trim(),
                providerTag);
            
            if (success)
            {
                AiTestResultPanel.Background = ThemeBrush("SuccessSurfaceBrush");
                AiTestResultText.Foreground = ThemeBrush("SuccessBrush");
                AiTestResultText.Text = message;
            }
            else
            {
                AiTestResultPanel.Background = ThemeBrush("DangerSurfaceBrush");
                AiTestResultText.Foreground = ThemeBrush("DangerBrush");
                AiTestResultText.Text = $"❌ {message}";
            }
        }
        catch (Exception ex)
        {
            AiTestResultPanel.Background = ThemeBrush("DangerSurfaceBrush");
            AiTestResultText.Foreground = ThemeBrush("DangerBrush");
            AiTestResultText.Text = $"❌ Erreur : {ex.Message}";
        }
        finally
        {
            AiTestBtn.IsEnabled = true;
        }
    }
    
    // === Gestionnaires d'événements - Navigateurs ===
    
    private void LoadBrowsersList()
    {
        BrowsersList.ItemsSource = _bookmarkService.GetSupportedBrowsers();
    }
    
    private async void ImportBrowserBookmarks_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string browserName }) return;
        
        try
        {
            var bookmarks = _bookmarkService.GetBookmarksForBrowser(browserName);
            
            if (bookmarks.Count == 0)
            {
                MessageBox.Show($"Aucun favori trouvé pour {browserName}.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            if (_indexingService == null)
            {
                MessageBox.Show("Service d'indexation non disponible.", "Erreur",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            // Réindexer pour inclure les nouveaux favoris
            await _indexingService.ReindexAsync();
            LoadBrowsersList(); // Rafraîchir les compteurs
            LoadStatistics();
            
            MessageBox.Show($"✅ {bookmarks.Count} favoris de {browserName} importés avec succès!", 
                "Import réussi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur lors de l'import: {ex.Message}", "Erreur",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    
    private async void ImportAllBrowserBookmarks_Click(object sender, RoutedEventArgs e)
    {
        if (_indexingService == null)
        {
            MessageBox.Show("Service d'indexation non disponible.", "Erreur",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        
        try
        {
            var bookmarks = _bookmarkService.GetAllBookmarks();
            
            if (bookmarks.Count == 0)
            {
                MessageBox.Show("Aucun favori trouvé dans les navigateurs installés.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            // S'assurer que l'option est activée (via le provider : jamais d'écriture d'une vieille copie)
            if (!_settings.Search.IndexBrowserBookmarks)
                Mutate(s => s.Search.IndexBrowserBookmarks = true);
            
            // Réindexer
            await _indexingService.ReindexAsync();
            LoadBrowsersList(); // Rafraîchir les compteurs
            LoadStatistics();
            
            MessageBox.Show($"✅ {bookmarks.Count} favoris importés depuis tous les navigateurs!", 
                "Import réussi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur lors de l'import: {ex.Message}", "Erreur",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // === Gestionnaires d'événements - Commandes Système ===
    
    private SystemControlCommand? _selectedSystemCommand;
    
    private void LoadSystemCommands()
    {
        try
        {
            SystemCommandsList.ItemsSource = null;
            AppCommandsList.ItemsSource = null;
            
            if (_settings.SystemCommands != null && _settings.SystemCommands.Count > 0)
            {
                // Séparer commandes système et commandes application
                var systemCommands = _settings.SystemCommands
                    .Where(c => !c.IsAppCommand)
                    .OrderBy(c => c.Category)
                    .ThenBy(c => c.Name)
                    .ToList();
                SystemCommandsList.ItemsSource = systemCommands;
                
                var appCommands = _settings.SystemCommands
                    .Where(c => c.IsAppCommand)
                    .OrderBy(c => c.Name)
                    .ToList();
                AppCommandsList.ItemsSource = appCommands;
            }
            
            LoadGuideCommands();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] ERREUR LoadSystemCommands: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Alimente les listes de l'onglet Guide à partir des commandes réellement
    /// définies. Le Guide ne peut donc plus se désynchroniser du code : toute
    /// commande ajoutée à DefaultSystemCommands y apparaît automatiquement.
    /// </summary>
    private void LoadGuideCommands()
    {
        // Repli sur les défauts si les réglages ne sont pas encore chargés,
        // pour ne jamais afficher un Guide vide.
        var source = _settings.SystemCommands is { Count: > 0 }
            ? _settings.SystemCommands
            : DefaultSystemCommands.Create();
        
        // Ordre conservé (celui de DefaultSystemCommands), groupé par catégorie.
        var systemView = new CollectionViewSource
        {
            Source = source.Where(c => !c.IsAppCommand).ToList()
        };
        systemView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(SystemControlCommand.Category)));
        
        GuideSystemCommandsList.ItemsSource = systemView.View;
        GuideAppCommandsList.ItemsSource = source.Where(c => c.IsAppCommand).ToList();
    }
    
    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabControl) return;
        
        try
        {
            var selectedIndex = MainTabControl.SelectedIndex;
            Debug.WriteLine($"[Settings] Onglet sélectionné: {selectedIndex}");
            
            // Recharger les données de l'onglet Commandes si sélectionné
            if (selectedIndex == 2 && !_isLoading)
            {
                LoadSystemCommands();
            }
            
            // Rafraîchir l'info du moteur de recherche quand l'onglet Indexation est sélectionné
            if (selectedIndex == 1 && !_isLoading)
            {
                LoadSearchEngineInfo(forceRefresh: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] ERREUR changement onglet: {ex.Message}");
            MessageBox.Show($"Erreur lors du changement d'onglet: {ex.Message}", "Erreur", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    
    private void SystemCommandItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SystemControlCommand cmd })
        {
            _selectedSystemCommand = cmd;
            CommandPrefixBox.Text = cmd.Prefix;
            CommandIconBox.Text = cmd.Icon;
            CommandPrefixLabel.Text = cmd.IsAppCommand ? "::" : ":";
            EditCommandName.Text = cmd.Name;
            EditCommandDescription.Text = cmd.Description;
            CommandEditPanel.Visibility = Visibility.Visible;
            
            // Focus sur le champ préfixe
            CommandPrefixBox.Focus();
            CommandPrefixBox.SelectAll();
        }
    }
    
    private void ToggleSwitch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SystemControlCommand cmd })
        {
            // Identité stable : le Type de commande (l'objet affiché peut venir d'un ancien snapshot)
            var type = cmd.Type;
            var enabled = !cmd.IsEnabled;
            Mutate(s =>
            {
                var target = s.SystemCommands.FirstOrDefault(c => c.Type == type);
                if (target != null) target.IsEnabled = enabled;
            });
            LoadSystemCommands();
            e.Handled = true; // Empêcher la propagation vers le parent
        }
    }
    
    private void CancelCommandEdit_Click(object sender, RoutedEventArgs e)
    {
        CommandEditPanel.Visibility = Visibility.Collapsed;
        _selectedSystemCommand = null;
    }
    
    private void ApplyCommandEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSystemCommand == null)
            return;
        
        var newPrefix = CommandPrefixBox.Text.Trim().ToLowerInvariant();
        var newIcon = CommandIconBox.Text.Trim();
        
        // Validation
        if (string.IsNullOrWhiteSpace(newPrefix))
        {
            MessageBox.Show("Le préfixe ne peut pas être vide.", "Erreur",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        // Vérifier les doublons
        var duplicate = _settings.SystemCommands.FirstOrDefault(c => 
            c.Type != _selectedSystemCommand.Type && 
            c.Prefix.Equals(newPrefix, StringComparison.OrdinalIgnoreCase));
        
        if (duplicate != null)
        {
            MessageBox.Show($"Le préfixe '{newPrefix}' est déjà utilisé par '{duplicate.Name}'.", "Erreur",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        // Appliquer les modifications sur le clone (identité stable : le Type de commande)
        var type = _selectedSystemCommand.Type;
        var icon = string.IsNullOrWhiteSpace(newIcon) ? "⚡" : newIcon;
        Mutate(s =>
        {
            var target = s.SystemCommands.FirstOrDefault(c => c.Type == type);
            if (target != null)
            {
                target.Prefix = newPrefix;
                target.Icon = icon;
            }
        });

        // Rafraîchir la liste
        LoadSystemCommands();
        CommandEditPanel.Visibility = Visibility.Collapsed;
        _selectedSystemCommand = null;
    }
    
    private void ResetSystemCommands_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Réinitialiser toutes les commandes système aux valeurs par défaut?", 
            "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            Mutate(s => s.ResetSystemCommands());
            LoadSystemCommands();
            CommandEditPanel.Visibility = Visibility.Collapsed;
        }
    }

    // === Gestionnaires d'événements - Réindexation auto ===
    
    private void AutoReindexEnabled_Changed(object sender, RoutedEventArgs e)
    {
        UpdateAutoReindexOptionsVisibility();
        if (!_isLoading)
        {
            var enabled = AutoReindexEnabledCheck.IsChecked == true;
            Mutate(s => s.Search.AutoReindexEnabled = enabled);
            
            // Reconfigurer le timer
            if (Application.Current is App app)
                app.SetupAutoReindex();
        }
    }
    
    private void ReindexMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            var mode = ReindexTimeRadio.IsChecked == true
                ? AutoReindexMode.ScheduledTime
                : AutoReindexMode.Interval;
            Mutate(s => s.Search.AutoReindexMode = mode);
            
            // Reconfigurer le timer
            if (Application.Current is App app)
                app.SetupAutoReindex();
        }
    }
    
    private void ReindexIntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading && ReindexIntervalCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            var minutes = int.Parse(tag);
            Mutate(s => s.Search.AutoReindexIntervalMinutes = minutes);
            
            if (Application.Current is App app)
                app.SetupAutoReindex();
        }
    }
    
    private void ReindexTimeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading)
        {
            var hour = GetComboTag(ReindexHourCombo) ?? "03";
            var minute = GetComboTag(ReindexMinuteCombo) ?? "00";
            var scheduled = $"{hour}:{minute}";
            Mutate(s => s.Search.AutoReindexScheduledTime = scheduled);
            
            if (Application.Current is App app)
                app.SetupAutoReindex();
        }
    }

    // === Gestionnaires d'événements - Actions ===
    
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Voulez-vous effacer tout l'historique de recherche?", "Confirmation",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            Mutate(s => s.Search.ClearSearchHistory());
            LoadStatistics();
            MessageBox.Show("Historique effacé!", "Succès", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    
    private async void Reindex_Click(object sender, RoutedEventArgs e)
    {
        if (_indexingService == null)
        {
            MessageBox.Show("Service d'indexation non disponible.", "Erreur",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        
        if (MessageBox.Show("Réindexer tous les fichiers maintenant?", "Réindexation",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            try
            {
                await _indexingService.ReindexAsync();
                LoadStatistics();
                MessageBox.Show("Réindexation terminée!", "Succès",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur: {ex.Message}", "Erreur",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
    
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(AppSettings.GetSettingsPath());
        if (folder != null && Directory.Exists(folder))
            Process.Start("explorer.exe", folder);
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("⚠️ Réinitialiser TOUS les paramètres?\n\nCette action est irréversible.",
            "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            AppSettings.Reset();
            MessageBox.Show("Paramètres réinitialisés!\nL'application va redémarrer.", "Succès",
                MessageBoxButton.OK, MessageBoxImage.Information);
            
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                Process.Start(exePath);
                Application.Current.Shutdown();
            }
        }
    }
    
    private static string? GetComboTag(ComboBox combo) => 
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private void UpdateStartupRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
            if (key == null) return;

            if (_settings.StartWithWindows)
            {
                var exePath = GetApplicationExecutablePath();
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Startup] ERREUR: {ex.Message}");
        }
    }

    private static string? GetApplicationExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && 
            processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !processPath.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var assemblyLocation = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            var directory = Path.GetDirectoryName(assemblyLocation);
            if (directory != null)
            {
                var exePath = Path.Combine(directory, $"{AppName}.exe");
                if (File.Exists(exePath))
                    return exePath;
            }
        }

        var mainModule = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(mainModule) && 
            mainModule.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !mainModule.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return mainModule;
        }

        return null;
    }

    public static void SyncStartupRegistry()
    {
        try
        {
            var settings = AppSettings.Load();
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
            if (key == null) return;

            var currentValue = key.GetValue(AppName) as string;
            var expectedPath = GetApplicationExecutablePath();

            if (settings.StartWithWindows)
            {
                if (!string.IsNullOrEmpty(expectedPath))
                {
                    var expectedValue = $"\"{expectedPath}\"";
                    if (currentValue != expectedValue)
                        key.SetValue(AppName, expectedValue);
                }
            }
            else if (currentValue != null)
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Startup] ERREUR sync: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Force le scroll du ScrollViewer principal même quand la souris est sur un élément enfant,
    /// sauf si l'événement vient d'un ScrollViewer imbriqué (commandes système ou application).
    /// </summary>
    private void TabScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;
        
        // Vérifier si l'événement vient d'un ScrollViewer imbriqué (commandes système ou application)
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source != scrollViewer)
        {
            if (source == SystemCommandsScrollViewer || source == AppCommandsScrollViewer)
            {
                // Le scroller imbriqué gère son propre scroll
                // Sauf s'il est déjà en haut/bas et ne peut plus scroller
                var sv = (ScrollViewer)source;
                if ((e.Delta > 0 && sv.VerticalOffset > 0) ||
                    (e.Delta < 0 && sv.VerticalOffset < sv.ScrollableHeight))
                    return;
                break;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }
}
