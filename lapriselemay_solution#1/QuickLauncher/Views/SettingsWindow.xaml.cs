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
    private readonly AppSettings _settings;
    private readonly ISettingsProvider? _settingsProvider;
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
        _settingsProvider = settingsProvider;
        _settings = settingsProvider?.Current ?? AppSettings.Load();
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
            _settings.Search.MaxResults = (int)e.NewValue;
            AutoSave();
        }
    }
    
    private void MaxHistorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MaxHistoryValue != null)
            MaxHistoryValue.Text = ((int)e.NewValue).ToString();
        
        if (!_isLoading)
        {
            _settings.Search.MaxSearchHistory = (int)e.NewValue;
            AutoSave();
        }
    }
    
    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue != null)
            OpacityValue.Text = $"{(int)(e.NewValue * 100)}%";
        
        if (!_isLoading)
        {
            _settings.Appearance.WindowOpacity = e.NewValue;
            AutoSave();
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
            _settings.Search.SearchDepth = (int)e.NewValue;
            AutoSave();
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
            _settings.Search.SystemSearchDepth = (int)e.NewValue;
            AutoSave();
        }
    }
    
    private void AnimationSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AnimationSpeedValue != null)
            AnimationSpeedValue.Text = $"{(int)e.NewValue} ms";
        
        if (!_isLoading)
        {
            _settings.Appearance.AnimationDurationMs = (int)e.NewValue;
            AutoSave();
        }
    }
    
    private void AnimationStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading && AnimationStyleCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            if (Enum.TryParse<AnimationStyle>(tag, out var style))
            {
                _settings.Appearance.AnimationStyle = style;
                AutoSave();
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
            _settings.Appearance.Theme = theme;
            AutoThemePanel.Visibility = theme == "Auto" ? Visibility.Visible : Visibility.Collapsed;
            
            // Appliquer le thème immédiatement (gère Dark, Light, Auto, System)
            _themeService?.ApplyTheme(theme);
            
            AutoSave();
        }
    }
    
    private void AutoThemeTime_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading)
        {
            if (LightStartCombo.SelectedItem is ComboBoxItem { Tag: string lightStart })
                _settings.Appearance.AutoThemeLightStart = lightStart;
            if (DarkStartCombo.SelectedItem is ComboBoxItem { Tag: string darkStart })
                _settings.Appearance.AutoThemeDarkStart = darkStart;
            
            // Réappliquer le thème si en mode Auto
            if (_settings.Appearance.Theme == "Auto")
            {
                ApplyAutoTheme();
            }
            
            AutoSave();
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
                _settings.Appearance.AccentColor = color;
                // Appliquer immédiatement, comme le fait ThemeModeCombo_SelectionChanged
                // pour le thème. Sans cet appel la couleur était bien persistée mais
                // n'atterrissait dans les ressources qu'au prochain démarrage (App.OnStartup)
                // ou au détour d'un changement de thème : titres de section, valeurs
                // accentuées et badges restaient sur l'ancienne couleur.
                _themeService?.ApplyAccentColor(color);
                AutoSave();
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
        _settings.Hotkey.UseAlt = HotkeyAltCheck.IsChecked == true;
        _settings.Hotkey.UseCtrl = HotkeyCtrlCheck.IsChecked == true;
        _settings.Hotkey.UseShift = HotkeyShiftCheck.IsChecked == true;
        _settings.Hotkey.UseWin = HotkeyWinCheck.IsChecked == true;
        _settings.Hotkey.Key = GetComboTag(HotkeyKeyCombo) ?? "Space";
        AutoSave();
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
            if (!_settings.Search.IndexedFolders.Contains(dialog.SelectedPath))
            {
                _settings.Search.IndexedFolders.Add(dialog.SelectedPath);
                RefreshFoldersList();
                AutoSave();
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
                _settings.Search.IndexedFolders.Remove(folder);
                RefreshFoldersList();
                AutoSave();
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
            _settings.Search.ResetIndexedFolders();
            RefreshFoldersList();
            AutoSave();
        }
    }
    
    private void RefreshFoldersList()
    {
        IndexedFoldersList.ItemsSource = null;
        IndexedFoldersList.ItemsSource = _settings.Search.IndexedFolders;
    }
    
    // === Sauvegarde automatique ===
    
    private void AutoSave()
    {
        if (_isLoading) return;
        
        if (_settingsProvider != null)
        {
            // Utiliser le provider centralisé pour sauvegarder ET notifier les abonnés
            _settingsProvider.Save();
        }
        else
        {
            _settings.Save();
        }
        Debug.WriteLine("[Settings] Auto-sauvegardé");
        
        // Afficher le feedback visuel
        ShowSaveIndicator();
    }
    
    /// <summary>
    /// Résout une brush du thème par sa clé (Styles.xaml).
    /// Centralise les couleurs d'état : un seul vert/orange/rouge dans toute
    /// l'application, au lieu des Color.FromRgb locaux qui divergeaient entre onglets.
    /// </summary>
    private Brush ThemeBrush(string key) => (Brush)FindResource(key);

    private void ShowSaveIndicator()
    {
        AutoSaveIndicator.Text = "✓ Sauvegardé";
        AutoSaveIndicator.Foreground = ThemeBrush("SuccessBrush");

        _saveIndicatorTimer.Stop();
        _saveIndicatorTimer.Start();
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
        
        // Synchroniser toutes les valeurs de checkboxes
        _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _settings.MinimizeOnStartup = MinimizeOnStartupCheck.IsChecked == true;
        _settings.CloseAfterLaunch = CloseAfterLaunchCheck.IsChecked == true;
        _settings.Appearance.ShowIndexingStatus = ShowIndexingStatusCheck.IsChecked == true;
        _settings.Appearance.ShowSettingsButton = ShowSettingsButtonCheck.IsChecked == true;
        _settings.SingleClickLaunch = SingleClickLaunchCheck.IsChecked == true;
        _settings.Search.EnableSearchHistory = EnableSearchHistoryCheck.IsChecked == true;
        _settings.Search.IndexHiddenFolders = IndexHiddenFoldersCheck.IsChecked == true;
        _settings.Appearance.ShowCategoryBadges = ShowCategoryBadgesCheck.IsChecked == true;
        _settings.Appearance.ShowGhostSuggestions = ShowGhostSuggestionsCheck.IsChecked == true;
        _settings.Appearance.EnableAnimations = EnableAnimationsCheck.IsChecked == true;
        _settings.Search.ScoringWeights.EnableRecencyBonus = EnableRecencyBonusCheck.IsChecked == true;
        
        UpdateAnimationSpeedPanelVisibility();
        UpdateStartupRegistry();
        AutoSave();
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
                _settings.Integrations.WeatherCity = resolvedCity;
                AutoSave();
                
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
            _settings.Integrations.WeatherUnit = unit;
            AutoSave();
        }
    }
    

    // === Gestionnaires d'événements - Assistant IA ===
    
    private void AiProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || AiProviderCombo.SelectedItem is not ComboBoxItem selected) return;
        
        var provider = selected.Tag?.ToString() ?? "chatgpt";
        _settings.Integrations.AiProvider = provider;
        
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
        
        _settings.Integrations.AiApiUrl = AiApiUrlBox.Text;
        _settings.Integrations.AiModel = AiModelBox.Text;
        UpdateAiFieldsState();
        AutoSave();
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
            _settings.Integrations.AiApiUrl = AiApiUrlBox.Text.Trim();
            AutoSave();
        }
    }
    
    private void AiApiKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            _settings.Integrations.AiApiKeyDecrypted = AiApiKeyBox.Password;
            AutoSave();
        }
    }
    
    private void AiModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            _settings.Integrations.AiModel = AiModelBox.Text.Trim();
            AutoSave();
        }
    }
    
    private void AiSystemPromptBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            _settings.Integrations.AiSystemPrompt = AiSystemPromptBox.Text;
            AutoSave();
        }
    }
    
    private void AiDebounceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || AiDebounceValue == null) return;
        var seconds = (int)e.NewValue;
        AiDebounceValue.Text = $"{seconds}s";
        _settings.Integrations.AiDebounceSeconds = seconds;
        AutoSave();
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
            
            // S'assurer que l'option est activée
            _settings.Search.IndexBrowserBookmarks = true;
            _settings.Save();
            
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] ERREUR LoadSystemCommands: {ex.Message}");
        }
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
            cmd.IsEnabled = !cmd.IsEnabled;
            LoadSystemCommands();
            AutoSave();
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
            c != _selectedSystemCommand && 
            c.Prefix.Equals(newPrefix, StringComparison.OrdinalIgnoreCase));
        
        if (duplicate != null)
        {
            MessageBox.Show($"Le préfixe '{newPrefix}' est déjà utilisé par '{duplicate.Name}'.", "Erreur",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        // Appliquer les modifications
        _selectedSystemCommand.Prefix = newPrefix;
        _selectedSystemCommand.Icon = string.IsNullOrWhiteSpace(newIcon) ? "⚡" : newIcon;
        
        // Rafraîchir la liste et sauvegarder
        LoadSystemCommands();
        AutoSave();
        CommandEditPanel.Visibility = Visibility.Collapsed;
        _selectedSystemCommand = null;
    }
    
    private void ResetSystemCommands_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Réinitialiser toutes les commandes système aux valeurs par défaut?", 
            "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _settings.ResetSystemCommands();
            LoadSystemCommands();
            AutoSave();
            CommandEditPanel.Visibility = Visibility.Collapsed;
        }
    }

    // === Gestionnaires d'événements - Réindexation auto ===
    
    private void AutoReindexEnabled_Changed(object sender, RoutedEventArgs e)
    {
        UpdateAutoReindexOptionsVisibility();
        if (!_isLoading)
        {
            _settings.Search.AutoReindexEnabled = AutoReindexEnabledCheck.IsChecked == true;
            AutoSave();
            
            // Reconfigurer le timer
            if (Application.Current is App app)
                app.SetupAutoReindex();
        }
    }
    
    private void ReindexMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            _settings.Search.AutoReindexMode = ReindexTimeRadio.IsChecked == true 
                ? AutoReindexMode.ScheduledTime 
                : AutoReindexMode.Interval;
            AutoSave();
            
            // Reconfigurer le timer
            if (Application.Current is App app)
                app.SetupAutoReindex();
        }
    }
    
    private void ReindexIntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading && ReindexIntervalCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _settings.Search.AutoReindexIntervalMinutes = int.Parse(tag);
            AutoSave();
            
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
            _settings.Search.AutoReindexScheduledTime = $"{hour}:{minute}";
            AutoSave();
            
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
            _settings.Search.ClearSearchHistory();
            _settings.Save();
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
