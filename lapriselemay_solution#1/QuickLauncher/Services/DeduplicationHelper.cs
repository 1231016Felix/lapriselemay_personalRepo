using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Utilitaire de déduplication des résultats de recherche.
/// Centralise la logique partagée entre IndexingService et SearchService
/// pour éviter les incohérences.
/// </summary>
public static class DeduplicationHelper
{
    /// <summary>
    /// Retourne une catégorie de type pour la déduplication des résultats.
    /// Application, StoreApp, SystemControl et AppControl sont fusionnés car un même programme
    /// peut apparaître via shell:AppsFolder (StoreApp), un raccourci .lnk (Application),
    /// WindowsSettingsProvider (SystemControl) ou les commandes internes (AppControl).
    /// </summary>
    public static ResultType GetCategory(ResultType type) => type switch
    {
        ResultType.StoreApp => ResultType.Application,
        ResultType.SystemControl => ResultType.Application,
        ResultType.AppControl => ResultType.Application,
        _ => type
    };

    /// <summary>
    /// Clé d'identité d'un résultat, utilisée par TOUTES les déduplications (index, recherche).
    /// <list type="bullet">
    ///   <item>Entrées « application installée » (raccourci .lnk, entrée de shell:AppsFolder,
    ///         page de paramètres, commande interne) : fusion par nom normalisé, car une même
    ///         application apparaît à la fois comme raccourci du menu Démarrer et dans AppsFolder.</item>
    ///   <item>Tout le reste (fichiers, dossiers, exécutables trouvés dans les dossiers de
    ///         l'utilisateur, scripts, favoris) : l'identité est le chemin (ou l'URL). Deux
    ///         « budget.xlsx » de deux dossiers clients, ou « rapport.docx » et « rapport.pdf »,
    ///         restent deux résultats distincts.</item>
    /// </list>
    /// </summary>
    /// <param name="item">Résultat à identifier.</param>
    /// <param name="normalizeName">Normalisation optionnelle du nom des entrées d'application
    /// (ex. suppression d'un emoji en tête).</param>
    public static string GetIdentityKey(SearchResult item, Func<string, string>? normalizeName = null)
    {
        if (IsInstalledAppEntry(item) || string.IsNullOrWhiteSpace(item.Path))
        {
            var name = normalizeName != null ? normalizeName(item.Name) : item.Name;
            return "app|" + (name ?? string.Empty).Trim().ToLowerInvariant();
        }

        return "path|" + NormalizePath(item.Path);
    }

    /// <summary>
    /// Vrai pour les entrées qui représentent une application installée plutôt qu'un fichier :
    /// apps de shell:AppsFolder, pages de paramètres, commandes internes et raccourcis .lnk.
    /// Un .exe trouvé dans Documents ou Téléchargements reste un fichier (identité = chemin).
    /// </summary>
    public static bool IsInstalledAppEntry(SearchResult item) => item.Type switch
    {
        ResultType.StoreApp or ResultType.SystemControl or ResultType.AppControl => true,
        ResultType.Application => item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    /// <summary>Chemin normalisé pour comparaison (casse, séparateur final, espaces).</summary>
    public static string NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().TrimEnd('\\', '/').ToLowerInvariant();
}
