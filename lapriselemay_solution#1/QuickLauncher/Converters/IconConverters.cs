using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using QuickLauncher.Models;

// UseWindowsForms importe implicitement System.Drawing, qui définit aussi Color et
// FontFamily : lever l'ambiguïté explicitement plutôt que de qualifier chaque usage.
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;

namespace QuickLauncher.Converters;

/// <summary>
/// Convertisseur pour afficher une icône native ou un emoji de fallback.
/// </summary>
public class IconToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasNativeIcon = value is ImageSource imageSource && imageSource != null;
        var isNativeIconParam = parameter as string == "Native";
        
        if (isNativeIconParam)
        {
            return hasNativeIcon ? Visibility.Visible : Visibility.Collapsed;
        }
        else // "Emoji" parameter - fallback
        {
            return hasNativeIcon ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Convertisseur pour l'inverse d'un booléen vers Visibility.
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Convertisseur multi-valeur pour déterminer si une icône native doit être affichée.
/// </summary>
public class NativeIconVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 1 && values[0] is ImageSource imageSource && imageSource != null)
        {
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Convertisseur pour masquer le contexte menu selon le type de résultat.
/// </summary>
public class ResultTypeToMenuVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ResultType type)
        {
            var menuItem = parameter as string;
            
            return menuItem switch
            {
                "RunAsAdmin" => type is ResultType.Application or ResultType.Script
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                "OpenWith" => type is ResultType.File 
                    ? Visibility.Visible 
                    : Visibility.Collapsed,
                "Terminal" => type is ResultType.File or ResultType.Folder 
                    ? Visibility.Visible 
                    : Visibility.Collapsed,
                "FileActions" => type is ResultType.Application or ResultType.File or 
                                 ResultType.Folder or ResultType.Script or ResultType.StoreApp
                    ? Visibility.Visible 
                    : Visibility.Collapsed,
                "Bookmark" => type is ResultType.Bookmark or ResultType.WebSearch
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                _ => Visibility.Visible
            };
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Convertisseur pour afficher/masquer selon si la valeur est null.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNull = value == null || (value is string s && string.IsNullOrEmpty(s));
        var inverse = parameter as string == "Inverse";
        
        if (inverse)
            return isNull ? Visibility.Visible : Visibility.Collapsed;
        
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Convertisseur pour afficher/masquer selon le type de prévisualisation.
/// </summary>
public class PreviewTypeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FilePreviewType previewType && parameter is string expectedType)
        {
            var matches = expectedType switch
            {
                "Image" => previewType == FilePreviewType.Image,
                "Text" => previewType == FilePreviewType.Text,
                "Folder" => previewType == FilePreviewType.Folder,
                "Application" => previewType == FilePreviewType.Application,
                "Audio" => previewType == FilePreviewType.Audio,
                "Video" => previewType == FilePreviewType.Video,
                "Archive" => previewType == FilePreviewType.Archive,
                "Document" => previewType == FilePreviewType.Document,
                "None" => previewType == FilePreviewType.None,
                _ => false
            };
            
            return matches ? Visibility.Visible : Visibility.Collapsed;
        }
        
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Convertisseur booléen vers icône de recherche.
/// </summary>
public class BoolToSearchIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isSearching && isSearching)
            return "⏳";
        return "🔍";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Choisit la police à utiliser pour une icône de repli selon le caractère affiché.
///
/// L'application mélange deux familles d'icônes : les glyphes vectoriels monochromes
/// de Segoe MDL2 Assets (repli par type de résultat — ils prennent la teinte du thème)
/// et les emojis couleur posés explicitement par certains services (météo, minuteries,
/// messages d'erreur), qui doivent rester en couleur.
///
/// Les glyphes MDL2 vivent dans la zone à usage privé Unicode (U+E000–U+F8FF), ce qui
/// permet de les distinguer d'un emoji sans que l'appelant ait à préciser sa famille.
/// </summary>
public class IconGlyphToFontFamilyConverter : IValueConverter
{
    private static readonly FontFamily SymbolFont = new("Segoe MDL2 Assets");
    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string { Length: 1 } s && s[0] is >= '\uE000' and <= '\uF8FF'
            ? SymbolFont
            : EmojiFont;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Convertisseur ResultType vers texte du badge de catégorie.
/// </summary>
public class ResultTypeToBadgeTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ResultType type)
        {
            return type switch
            {
                ResultType.Application => "APP",
                ResultType.StoreApp => "STORE",
                ResultType.File => "FILE",
                ResultType.Folder => "DIR",
                ResultType.Script => "SCRIPT",
                ResultType.WebSearch => "WEB",
                ResultType.Command => "CMD",
                ResultType.Calculator => "CALC",
                ResultType.SystemCommand => "SYS",
                ResultType.SearchHistory => "HIST",
                ResultType.SystemControl => "CTRL SYS",
                ResultType.AppControl => "CTRL APP",
                ResultType.Bookmark => "FAV",
                ResultType.Note => "NOTE",
                _ => "?"
            };
        }
        return "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Convertisseur ResultType vers couleur du badge de catégorie.
/// </summary>
public class ResultTypeToBadgeColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => BadgePalette.BackgroundBrush(value as ResultType? ?? ResultType.File);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Convertisseur ResultType vers couleur de TEXTE du badge de catégorie.
/// Choisit noir ou blanc selon ce qui contraste le mieux avec le fond du badge.
/// </summary>
public class ResultTypeToBadgeForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => BadgePalette.ForegroundBrush(value as ResultType? ?? ResultType.File);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Palette des badges de catégorie : couleur de fond par type, et couleur de texte
/// calculée pour maximiser le contraste.
///
/// <b>Pourquoi calculer le texte :</b> le blanc était codé en dur dans le XAML, ce qui
/// donnait 1.55:1 sur le jaune doré des favoris et 1.72:1 sur le jaune des dossiers —
/// très en dessous du minimum lisible de 4.5:1. Le noir y atteint respectivement
/// 13.5:1 et 12.2:1. Dériver la couleur du fond règle aussi le cas des futurs types.
///
/// Les brushes sont créés une fois et figés (<see cref="Freezable.Freeze"/>) : ces
/// convertisseurs sont évalués pour chaque item à chaque frappe.
/// </summary>
internal static class BadgePalette
{
    private static readonly Color FallbackColor = Color.FromRgb(0x66, 0x66, 0x66);

    private static readonly Dictionary<ResultType, Color> BadgeColors = new()
    {
        { ResultType.Application, Color.FromRgb(0x10, 0x7C, 0x10) },   // Vert
        { ResultType.StoreApp, Color.FromRgb(0x00, 0x78, 0xD4) },      // Bleu Windows
        { ResultType.File, Color.FromRgb(0x88, 0x88, 0x88) },          // Gris
        { ResultType.Folder, Color.FromRgb(0xFF, 0xB9, 0x00) },        // Jaune/Orange
        { ResultType.Script, Color.FromRgb(0xE8, 0x11, 0x23) },        // Rouge
        { ResultType.WebSearch, Color.FromRgb(0x00, 0x78, 0xD4) },     // Bleu
        { ResultType.Command, Color.FromRgb(0x68, 0x21, 0x7A) },       // Violet
        { ResultType.Calculator, Color.FromRgb(0x00, 0x99, 0xBC) },    // Cyan
        { ResultType.SystemCommand, Color.FromRgb(0x68, 0x21, 0x7A) }, // Violet
        { ResultType.SearchHistory, Color.FromRgb(0x66, 0x66, 0x66) }, // Gris foncé
        { ResultType.SystemControl, Color.FromRgb(0xFF, 0x8C, 0x00) }, // Orange
        { ResultType.AppControl, Color.FromRgb(0x00, 0x96, 0x88) },    // Teal
        { ResultType.Bookmark, Color.FromRgb(0xFF, 0xC8, 0x00) },      // Jaune doré
        { ResultType.Note, Color.FromRgb(0xE3, 0x00, 0x8C) }           // Rose
    };

    private static readonly Dictionary<ResultType, SolidColorBrush> Backgrounds = [];
    private static readonly Dictionary<ResultType, SolidColorBrush> Foregrounds = [];

    static BadgePalette()
    {
        foreach (var type in Enum.GetValues<ResultType>())
        {
            var bg = BadgeColors.TryGetValue(type, out var c) ? c : FallbackColor;
            Backgrounds[type] = Frozen(bg);
            Foregrounds[type] = Frozen(BestForeground(bg));
        }
    }

    public static SolidColorBrush BackgroundBrush(ResultType type)
        => Backgrounds.TryGetValue(type, out var b) ? b : Frozen(FallbackColor);

    public static SolidColorBrush ForegroundBrush(ResultType type)
        => Foregrounds.TryGetValue(type, out var b) ? b : Frozen(Colors.White);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Marge exigée pour préférer le noir au blanc.
    ///
    /// Prendre systématiquement le meilleur des deux faisait basculer en noir des fonds
    /// où le gain était négligeable — le bleu Windows (4.53 → 4.64, soit 1.02×) ou le rose
    /// des notes (1.03×) — pour un rendu qui heurte la convention sans rien apporter.
    /// Sur cette palette les gains se répartissent en deux groupes nets, 0.21–1.03× et
    /// 1.56–8.70× : le seuil tombe dans le vide entre les deux, aucun badge n'est limite.
    /// </summary>
    private const double BlackPreferenceMargin = 1.15;

    /// <summary>
    /// Retourne la couleur de texte la plus lisible sur <paramref name="background"/>,
    /// en privilégiant le blanc sauf si le noir apporte un gain net (voir
    /// <see cref="BlackPreferenceMargin"/>).
    ///
    /// Les ratios suivent la formule WCAG (L+0.05)/(L'+0.05), avec L=1 pour le blanc
    /// et L=0 pour le noir. Le pire cas de la palette reste à 4.52:1, au-dessus du
    /// minimum AA de 4.5:1 pour le texte.
    /// </summary>
    private static Color BestForeground(Color background)
    {
        var luminance = RelativeLuminance(background);
        var contrastWithWhite = 1.05 / (luminance + 0.05);
        var contrastWithBlack = (luminance + 0.05) / 0.05;

        return contrastWithBlack >= contrastWithWhite * BlackPreferenceMargin
            ? Colors.Black
            : Colors.White;
    }

    /// <summary>
    /// Luminance relative WCAG 2.1 (composantes linéarisées puis pondérées).
    /// </summary>
    private static double RelativeLuminance(Color c)
        => 0.2126 * Linearize(c.R) + 0.7152 * Linearize(c.G) + 0.0722 * Linearize(c.B);

    private static double Linearize(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}

/// <summary>
/// Convertit l'AlternationIndex (0-based) en texte de raccourci "Alt+1" à "Alt+9".
/// Retourne une chaîne vide pour les index >= 9.
/// </summary>
public class AlternationToShortcutConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int index and >= 0 and < 9)
            return $"Alt+{index + 1}";
        return "";
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Rend visible uniquement les éléments avec AlternationIndex 0-8 (Alt+1 à Alt+9).
/// </summary>
public class AlternationToShortcutVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is int index and >= 0 and < 9
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
    
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
