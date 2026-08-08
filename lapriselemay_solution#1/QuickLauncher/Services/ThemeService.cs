using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickLauncher.Models;

using WpfApplication = System.Windows.Application;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace QuickLauncher.Services;

/// <summary>
/// Service de gestion des thèmes de l'application.
/// Supporte les thèmes Sombre, Clair, Système (suit Windows) et Auto (selon l'heure).
/// </summary>
public class ThemeService : IThemeService, IDisposable
{
    private readonly ISettingsProvider _settingsProvider;
    private string _currentTheme = "Dark";
    private DispatcherTimer? _autoThemeTimer;

    /// <inheritdoc/>
    public event EventHandler<string>? ThemeChanged;

    /// <inheritdoc/>
    public string CurrentTheme => _currentTheme;

    public ThemeService(ISettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }
    /// <inheritdoc/>
    public void Initialize()
    {
        var settings = _settingsProvider.Current;
        ApplyThemeFromSettings(settings);
        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
        StartAutoThemeTimer();
    }

    /// <inheritdoc/>
    public void ApplyThemeFromSettings(AppSettings? settings = null)
    {
        settings ??= _settingsProvider.Current;

        var actualTheme = settings.Appearance.Theme switch
        {
            "Light" => "Light",
            "Auto" => GetAutoTheme(settings),
            _ => "Dark"
        };

        ApplyThemeInternal(actualTheme);
    }

    /// <inheritdoc/>
    public void ApplyTheme(string theme)
    {
        var actualTheme = theme;
        if (theme.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            actualTheme = IsWindowsInLightMode() ? "Light" : "Dark";
        }
        else if (theme.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            var settings = _settingsProvider.Current;
            actualTheme = GetAutoTheme(settings);
        }

        ApplyThemeInternal(actualTheme);
    }

    /// <inheritdoc/>
    public void ApplyAccentColor(string hexColor)
    {
        if (string.IsNullOrEmpty(hexColor)) return;

        try
        {
            var color = (WpfColor)WpfColorConverter.ConvertFromString(hexColor);
            var app = WpfApplication.Current;
            if (app == null) return;

            app.Resources["AccentColor"] = color;
            app.Resources["AccentBrush"] = new SolidColorBrush(color);

            var hoverColor = LightenColor(color, 0.1);
            var pressedColor = DarkenColor(color, 0.1);
            app.Resources["AccentHoverColor"] = hoverColor;
            app.Resources["AccentPressedColor"] = pressedColor;
            app.Resources["AccentHoverBrush"] = new SolidColorBrush(hoverColor);

            // La surbrillance de la ligne sélectionnée est l'accent en transparence.
            // Sans cette ligne elle restait bleue même après le choix d'un autre accent,
            // car SelectionBrush vient du dictionnaire de thème et n'était jamais régénéré.
            // Le fond clair sature plus vite que le sombre : 0x4D sur blanc et 0x66 sur
            // noir donnent un poids visuel équivalent (~1.5:1 contre le fond dans les deux cas).
            var selectionAlpha = _currentTheme == "Light" ? (byte)0x4D : (byte)0x66;
            app.Resources["SelectionBrush"] =
                new SolidColorBrush(WpfColor.FromArgb(selectionAlpha, color.R, color.G, color.B));
        }
        catch
        {
            // Couleur invalide, ignorer
        }
    }

    /// <inheritdoc/>
    public bool IsWindowsInLightMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            if (key != null)
            {
                var value = key.GetValue("AppsUseLightTheme");
                if (value is int intValue)
                    return intValue == 1;
            }
        }
        catch { }

        return false;
    }

    /// <inheritdoc/>
    public string GetThemeModeDescription(ThemeMode mode, AppSettings? settings = null)
    {
        return mode switch
        {
            ThemeMode.Light => "Clair",
            ThemeMode.Auto => GetAutoThemeDescription(settings),
            _ => "Sombre"
        };
    }

    /// <inheritdoc/>
    public void Shutdown()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
        _autoThemeTimer?.Stop();
        _autoThemeTimer = null;
    }

    public void Dispose()
    {
        Shutdown();
        GC.SuppressFinalize(this);
    }

    #region Private Helpers

    private string GetAutoThemeDescription(AppSettings? settings)
    {
        settings ??= _settingsProvider.Current;
        var current = GetAutoTheme(settings);
        var lightStart = settings.Appearance.AutoThemeLightStart ?? "07:00";
        var darkStart = settings.Appearance.AutoThemeDarkStart ?? "19:00";
        return $"Auto ({current}) — Clair {lightStart}, Sombre {darkStart}";
    }

    private string GetAutoTheme(AppSettings settings)
    {
        var now = DateTime.Now.TimeOfDay;

        if (TimeSpan.TryParse(settings.Appearance.AutoThemeLightStart ?? "07:00", out var lightStart) &&
            TimeSpan.TryParse(settings.Appearance.AutoThemeDarkStart ?? "19:00", out var darkStart))
        {
            if (lightStart < darkStart)
                return (now >= lightStart && now < darkStart) ? "Light" : "Dark";
            else
                return (now >= lightStart || now < darkStart) ? "Light" : "Dark";
        }

        return "Dark";
    }

    private void ApplyThemeInternal(string theme)
    {
        if (theme == _currentTheme && WpfApplication.Current?.Resources.MergedDictionaries.Count > 0)
            return;

        _currentTheme = theme;

        var app = WpfApplication.Current;
        if (app == null) return;

        var themeUri = theme == "Light"
            ? new Uri("Resources/Themes/LightTheme.xaml", UriKind.Relative)
            : new Uri("Resources/Themes/DarkTheme.xaml", UriKind.Relative);

        try
        {
            var themeDict = new System.Windows.ResourceDictionary { Source = themeUri };

            var existingTheme = app.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source?.OriginalString.Contains("Theme.xaml") == true);

            if (existingTheme != null)
                app.Resources.MergedDictionaries.Remove(existingTheme);

            app.Resources.MergedDictionaries.Add(themeDict);

            // Le nouveau dictionnaire réintroduit un SelectionBrush bleu par défaut,
            // et avec un alpha calibré pour l'autre thème. Réappliquer l'accent
            // le régénère aux bonnes valeurs — indispensable pour les bascules
            // automatiques (thème Auto, changement de thème Windows) qui ne passent
            // pas par la fenêtre de paramètres.
            ApplyAccentColor(_settingsProvider.Current.Appearance.AccentColor);

            ThemeChanged?.Invoke(this, theme);
        }
        catch
        {
            // Fallback silencieux si le fichier de thème n'existe pas
        }
    }

    private void StartAutoThemeTimer()
    {
        _autoThemeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _autoThemeTimer.Tick += (_, _) =>
        {
            var settings = _settingsProvider.Current;
            if (settings.Appearance.Theme == "Auto")
                ApplyThemeFromSettings(settings);
        };
        _autoThemeTimer.Start();
    }

    private void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            var settings = _settingsProvider.Current;
            if (settings.Appearance.Theme == "System" || settings.Appearance.ThemeMode == ThemeMode.Auto)
                ApplyThemeFromSettings(settings);
        }
    }

    private static WpfColor LightenColor(WpfColor color, double factor)
    {
        return WpfColor.FromArgb(
            color.A,
            (byte)Math.Min(255, color.R + (255 - color.R) * factor),
            (byte)Math.Min(255, color.G + (255 - color.G) * factor),
            (byte)Math.Min(255, color.B + (255 - color.B) * factor));
    }

    private static WpfColor DarkenColor(WpfColor color, double factor)
    {
        return WpfColor.FromArgb(
            color.A,
            (byte)(color.R * (1 - factor)),
            (byte)(color.G * (1 - factor)),
            (byte)(color.B * (1 - factor)));
    }

    #endregion
}