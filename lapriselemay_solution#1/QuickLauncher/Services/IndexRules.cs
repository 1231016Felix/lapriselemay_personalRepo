using System.IO;

namespace QuickLauncher.Services;

/// <summary>
/// Règles d'éligibilité partagées par le scan complet (<see cref="IndexingService"/>) et
/// le traitement incrémental des événements du <see cref="FileWatcherService"/>, pour que
/// les deux chemins indexent exactement les mêmes fichiers : même profondeur maximale,
/// même traitement des dossiers cachés, mêmes extensions, mêmes fichiers temporaires exclus.
/// </summary>
public static class IndexRules
{
    /// <summary>Retire le séparateur final (« C:\Data\ » → « C:\Data »).</summary>
    public static string NormalizeDirectory(string path) => path.TrimEnd('\\', '/');

    /// <summary>
    /// Vrai si <paramref name="path"/> est situé SOUS <paramref name="folder"/>.
    /// La frontière de segment est respectée : « C:\Database » n'est pas sous « C:\Data ».
    /// </summary>
    public static bool IsUnder(string path, string folder)
    {
        var f = NormalizeDirectory(folder);
        return path.Length > f.Length + 1
            && path.StartsWith(f, StringComparison.OrdinalIgnoreCase)
            && (path[f.Length] == '\\' || path[f.Length] == '/');
    }

    public static bool IsUnderAny(string path, IEnumerable<string> folders)
        => folders.Any(f => IsUnder(path, f));

    /// <summary>
    /// Racine indexée la plus profonde contenant le chemin (ou égale à lui), normalisée ;
    /// <c>null</c> si le chemin n'appartient à aucune racine.
    /// </summary>
    public static string? FindRoot(string path, IEnumerable<string> roots)
    {
        string? best = null;
        foreach (var root in roots)
        {
            var r = NormalizeDirectory(root);
            if (r.Length == 0) continue;
            var matches = NormalizeDirectory(path).Equals(r, StringComparison.OrdinalIgnoreCase) || IsUnder(path, r);
            if (matches && (best == null || r.Length > best.Length))
                best = r;
        }
        return best;
    }

    /// <summary>
    /// Profondeur d'un dossier par rapport à sa racine, avec la convention du scan complet :
    /// 0 pour la racine elle-même, 1 pour ses sous-dossiers directs, etc.
    /// </summary>
    public static int DirectoryDepth(string root, string directory)
    {
        var r = NormalizeDirectory(root);
        var d = NormalizeDirectory(directory);
        if (d.Length <= r.Length + 1) return 0;
        return d[(r.Length + 1)..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>Fichiers temporaires jamais indexés (.tmp*, .temp, verrous Office « ~$ »).</summary>
    public static bool IsTemporaryFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.StartsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".temp", StringComparison.OrdinalIgnoreCase)
            || path.Contains("~$", StringComparison.Ordinal);
    }

    /// <summary>
    /// Le fichier serait-il indexé par le scan complet ? (extension, profondeur relative à la
    /// racine la plus proche, fichier ou dossier ancêtre caché si l'option est désactivée).
    /// </summary>
    public static bool IsEligibleFile(
        string path,
        IEnumerable<string> roots,
        ICollection<string> extensions,
        int searchDepth,
        bool includeHidden,
        Func<string, FileAttributes?> getAttributes)
    {
        if (string.IsNullOrEmpty(path) || IsTemporaryFile(path)) return false;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!extensions.Contains(ext)) return false;

        var root = FindRoot(path, roots);
        var directory = Path.GetDirectoryName(path);
        if (root == null || directory == null) return false;
        if (DirectoryDepth(root, directory) > searchDepth) return false;

        if (!includeHidden && (IsHidden(getAttributes(path)) || HasHiddenAncestor(directory, root, getAttributes)))
            return false;

        return true;
    }

    /// <summary>
    /// Un dossier (créé, copié, renommé ou déplacé) est-il dans la portée du scan complet ?
    /// </summary>
    public static bool IsEligibleDirectory(
        string directory,
        IEnumerable<string> roots,
        int searchDepth,
        bool includeHidden,
        Func<string, FileAttributes?> getAttributes)
    {
        var root = FindRoot(directory, roots);
        if (root == null) return false;
        if (DirectoryDepth(root, directory) > searchDepth) return false;
        return includeHidden || !HasHiddenAncestor(directory, root, getAttributes);
    }

    /// <summary>Attributs du chemin, ou <c>null</c> s'il est inaccessible ou a disparu.</summary>
    public static FileAttributes? TryGetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch { return null; }
    }

    private static bool IsHidden(FileAttributes? attributes)
        => attributes is { } a && (a & FileAttributes.Hidden) != 0;

    /// <summary>Le dossier ou l'un de ses ancêtres jusqu'à la racine incluse est-il caché ?</summary>
    private static bool HasHiddenAncestor(string directory, string root, Func<string, FileAttributes?> getAttributes)
    {
        for (var current = NormalizeDirectory(directory); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (IsHidden(getAttributes(current))) return true;
            if (NormalizeDirectory(current).Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return false;
    }
}
