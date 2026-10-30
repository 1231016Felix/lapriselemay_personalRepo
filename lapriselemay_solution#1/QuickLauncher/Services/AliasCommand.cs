using System.IO;
using System.Text.RegularExpressions;

namespace QuickLauncher.Services;

/// <summary>
/// Interprétation de la cible TEXTUELLE d'un alias (alias par défaut, alias importés, alias
/// créés avant que le type d'origine soit mémorisé). La cible n'est JAMAIS passée brute à
/// « cmd.exe /c » : un chemin avec espaces, accents ou « &amp; » reste un chemin, et ShellExecute
/// consulte les App Paths et les protocoles que l'invite de commandes ignore (msedge,
/// devenv, ms-settings:…). Seule une cible préfixée par « &gt; » est une commande shell
/// volontaire, exécutée par cmd.exe.
/// </summary>
public static partial class AliasCommand
{
    /// <summary>Préfixe explicite d'une commande shell (ex. « &gt;ipconfig /all | clip »).</summary>
    public const char ShellPrefix = '>';

    /// <summary>La cible est-elle une commande shell volontaire (préfixe « &gt; ») ?</summary>
    public static bool IsShellCommand(string target) => target.TrimStart().StartsWith(ShellPrefix);

    /// <summary>Commande shell sans son préfixe.</summary>
    public static string GetShellCommand(string target) => target.TrimStart()[1..].Trim();

    /// <summary>
    /// Découpe une cible en (programme, arguments) pour ShellExecute.
    /// <list type="bullet">
    ///   <item>Fichier ou dossier existant, même avec espaces : la cible seule.</item>
    ///   <item>URI (ms-settings:, https://, steam://…) : la cible seule.</item>
    ///   <item>« "C:\Program Files\App\app.exe" --flag » : programme entre guillemets + arguments.</item>
    ///   <item>« C:\Program Files\App\app.exe --flag » : le plus long préfixe existant + le reste.</item>
    ///   <item>« code C:\projet » : premier mot + le reste.</item>
    /// </list>
    /// </summary>
    /// <param name="target">Cible textuelle de l'alias.</param>
    /// <param name="pathExists">Test d'existence (fichier ou dossier), injecté pour les tests.</param>
    public static (string FileName, string? Arguments) Parse(string target, Func<string, bool> pathExists)
    {
        var text = target.Trim();
        if (text.Length == 0)
            throw new ArgumentException("Cible d'alias vide.", nameof(target));

        if (pathExists(text) || UriScheme().IsMatch(text))
            return (text, null);

        if (text[0] == '"')
        {
            var closing = text.IndexOf('"', 1);
            if (closing > 1)
                return (text[1..closing], NullIfEmpty(text[(closing + 1)..]));
        }

        // Chemin non protégé suivi d'arguments : le plus long préfixe qui existe
        for (var i = text.LastIndexOf(' '); i > 0; i = text.LastIndexOf(' ', i - 1))
        {
            var candidate = text[..i].TrimEnd();
            if (candidate.Length > 0 && pathExists(candidate))
                return (candidate, NullIfEmpty(text[(i + 1)..]));
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, null) : (text[..space], NullIfEmpty(text[(space + 1)..]));
    }

    /// <summary>
    /// Résout un nom de programme nu (« code », « wt ») comme l'invite de commandes (PATH +
    /// PATHEXT), pour garder le comportement des alias par défaut. Sans résultat, le nom est
    /// rendu tel quel : ShellExecute consultera alors les App Paths (msedge, devenv…).
    /// </summary>
    public static string ResolveOnPath(string fileName)
    {
        if (fileName.IndexOfAny(['\\', '/', ':']) >= 0 || Path.HasExtension(fileName))
            return fileName;

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), fileName + extension);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // Entrée de PATH invalide : l'ignorer
                }
            }
        }

        return fileName;
    }

    private static string? NullIfEmpty(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Schéma d'URI d'au moins deux caractères : exclut les lettres de lecteur (« C: »).</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]+:")]
    private static partial Regex UriScheme();
}
