using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace QuickLauncher.Models;

/// <summary>
/// Types de résultats de recherche.
/// </summary>
public enum ResultType
{
    Application,
    StoreApp,
    File,
    Folder,
    Script,
    WebSearch,
    Command,
    Calculator,
    SystemCommand,
    SearchHistory,
    SystemControl,
    AppControl,  // Contrôles application (::settings, ::quit, etc.)
    Bookmark,  // Favoris des navigateurs (Chrome, Edge, Firefox)
    Note       // Notes rapides de l'utilisateur
}

/// <summary>
/// Résultat de recherche avec scoring et métadonnées.
/// 
/// DTO pur : ne contient plus de logique de chargement d'icônes.
/// Le chargement est délégué à <see cref="Services.IIconLoader"/> (Amélioration #1).
/// Implémente INotifyPropertyChanged uniquement pour la notification WPF de NativeIcon.
/// </summary>
public sealed class SearchResult : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    
    private string _name = string.Empty;
    private string? _normalizedName;

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            _normalizedName = null; // invalider le cache de normalisation
        }
    }

    /// <summary>
    /// Nom normalisé pour le fuzzy matching (emojis retirés + minuscules), calculé une
    /// seule fois puis mis en cache. Évite de refaire StripEmojis + ToLowerInvariant pour
    /// chaque item à chaque frappe dans le chemin chaud de la recherche.
    /// </summary>
    public string NormalizedName =>
        _normalizedName ??= Services.SearchAlgorithms.StripEmojis(_name).ToLowerInvariant();

    public string Path { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ResultType Type { get; set; }
    
    /// <summary>
    /// Si true, masque l'icône et le badge de catégorie (bloc info unifié, ex: météo).
    /// </summary>
    public bool IsInfoBlock { get; set; }
    public int Score { get; set; }
    public DateTime LastUsed { get; set; }
    public int UseCount { get; set; }
    
    private string? _customIcon;
    private ImageSource? _nativeIcon;
    
    /// <summary>
    /// Icône native extraite du fichier (ImageSource).
    /// Setter déclenche PropertyChanged pour mettre à jour le binding WPF.
    /// Le chargement est géré externalement par <see cref="Services.IIconLoader"/>.
    /// </summary>
    public ImageSource? NativeIcon
    {
        get => _nativeIcon;
        set
        {
            _nativeIcon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNativeIcon));
        }
    }
    
    /// <summary>
    /// Indique si une icône native valide est disponible.
    /// </summary>
    public bool HasNativeIcon => _nativeIcon != null;
    
    /// <summary>
    /// Icône emoji de fallback.
    /// </summary>
    public string DisplayIcon
    {
        get => _customIcon ?? GetDefaultIcon();
        set => _customIcon = value;
    }
    
    /// <summary>
    /// Crée une copie détachée de ce résultat, destinée à l'affichage.
    ///
    /// <b>Pourquoi c'est nécessaire :</b> les objets du cache d'indexation vivent aussi
    /// longtemps que l'application. Si l'UI leur attachait directement <see cref="NativeIcon"/>,
    /// chaque icône affichée resterait référencée à vie par le cache d'index (non borné),
    /// ce qui contourne les plafonds de 500 entrées de IconCacheService et IconExtractorService.
    /// Le clone est jeté au changement de recherche, donc l'icône redevient collectable.
    ///
    /// Cela isole aussi le cache des écritures concurrentes de <see cref="Score"/>
    /// faites par le scoring PLINQ pendant que le thread UI lit la collection.
    ///
    /// <see cref="NativeIcon"/> n'est volontairement PAS copié : il est rechargé depuis
    /// les caches d'icônes (accès dictionnaire) par <see cref="Services.IIconLoader"/>.
    /// </summary>
    public SearchResult Clone()
    {
        var clone = new SearchResult
        {
            Path = Path,
            Description = Description,
            Type = Type,
            IsInfoBlock = IsInfoBlock,
            Score = Score,
            LastUsed = LastUsed,
            UseCount = UseCount
        };

        // Champs privés copiés directement pour préserver le cache de normalisation
        // et ne pas matérialiser l'emoji par défaut dans _customIcon.
        clone._name = _name;
        clone._normalizedName = _normalizedName;
        clone._customIcon = _customIcon;

        return clone;
    }

    private string GetDefaultIcon() => GetDefaultIcon(Type);

    /// <summary>
    /// Icône de repli pour un type de résultat, en glyphes <b>Segoe MDL2 Assets</b>.
    ///
    /// Les emojis précédents ignoraient totalement le thème et la couleur d'accent, et leur
    /// poids optique variait fortement d'un glyphe à l'autre — alors que le reste de
    /// l'interface (loupe, croix, engrenage) est déjà en MDL2. Ces glyphes-ci sont
    /// monochromes et prennent donc la teinte définie par le thème.
    ///
    /// Les codes ci-dessous ont été vérifiés visuellement dans la police installée.
    /// Les emojis posés explicitement ailleurs (météo, minuteries, erreurs) restent
    /// des emojis : <see cref="Converters.IconGlyphToFontFamilyConverter"/> choisit
    /// la bonne police à l'affichage.
    /// </summary>
    public static string GetDefaultIcon(ResultType type) => type switch
    {
        ResultType.Application => "",    // fenêtre
        ResultType.StoreApp => "",       // sac de courses (Store)
        ResultType.File => "",           // page
        ResultType.Folder => "",         // dossier
        ResultType.Script => "",         // accolades (code)
        ResultType.WebSearch => "",      // globe
        ResultType.Command => "",        // invite de commande
        ResultType.Calculator => "",     // calculatrice
        ResultType.SystemCommand => "",  // engrenage
        ResultType.SearchHistory => "",  // horloge avec flèche (historique)
        ResultType.SystemControl => "",  // engrenages multiples
        ResultType.AppControl => "",     // clé à molette
        ResultType.Bookmark => "",       // étoile
        ResultType.Note => "",           // note
        _ => ""                          // épingle
    };
    
    public override string ToString() => $"{DisplayIcon} {Name}";
}
