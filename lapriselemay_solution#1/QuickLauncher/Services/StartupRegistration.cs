using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace QuickLauncher.Services;

/// <summary>
/// Démarrage avec Windows : valeur « QuickLauncher » de la clé HKCU\...\Run, qui
/// désigne l'exécutable lancé à l'ouverture de session. Elle doit toujours désigner
/// une copie gérée par Velopack (installée par le Setup, ou portable), jamais une
/// sortie de build.
///
/// Un build lancé depuis Visual Studio (bin\Debug, bin\Release) ou par <c>dotnet run</c>
/// n'y inscrit donc jamais son propre chemin : sinon Windows relance ce build à chaque
/// session, et le processus verrouille la sortie du build suivant (MSB3027 « verrouillé
/// par QuickLauncher »). S'il y trouve son propre chemin, laissé par une version
/// antérieure, il rend l'inscription à la version installée, ou la retire faute de
/// version installée : celle-ci ne peut pas la reprendre elle-même tant que ce build
/// tourne, puisque l'instance unique l'arrête avant OnStartup. Décocher « Démarrer avec
/// Windows » retire la valeur depuis n'importe quelle copie.
/// </summary>
public static class StartupRegistration
{
    /// <summary>Modification à apporter à la valeur de démarrage.</summary>
    public enum Change
    {
        None,
        Write,
        Delete,
    }

    /// <summary>
    /// Décide quoi faire de la valeur de démarrage, sans toucher au registre.
    /// Les valeurs sont des chemins entre guillemets, comparés sans tenir compte de la casse.
    /// </summary>
    /// <param name="startWithWindows">Réglage « Démarrer avec Windows ».</param>
    /// <param name="isVelopackCopy">
    /// Vrai si l'app tourne depuis une copie Velopack, installée ou portable
    /// (<see cref="UpdateService.IsInstalled"/>).
    /// </param>
    /// <param name="currentValue">Valeur actuelle de la clé, ou null si absente.</param>
    /// <param name="ownValue">Valeur qui désigne cette copie, ou null si son chemin est inconnu.</param>
    /// <param name="installedValue">
    /// Valeur qui désigne la version installée par le Setup, ou null si elle est absente.
    /// Utilisée seulement par un build de dev.
    /// </param>
    /// <returns>La modification, et la valeur à écrire pour <see cref="Change.Write"/>.</returns>
    public static (Change Change, string? Value) Decide(
        bool startWithWindows, bool isVelopackCopy, string? currentValue, string? ownValue, string? installedValue)
    {
        // Option décochée : n'importe quelle copie retire la valeur.
        if (!startWithWindows)
            return currentValue != null ? (Change.Delete, null) : (Change.None, null);

        // Copie Velopack : elle s'inscrit elle-même.
        if (isVelopackCopy)
            return ownValue != null && !SameValue(currentValue, ownValue) ? (Change.Write, ownValue) : (Change.None, null);

        // Build de dev : il ne touche pas à une valeur qui désigne une autre copie.
        var heldByThisBuild = ownValue != null && SameValue(currentValue, ownValue);
        if (currentValue != null && !heldByThisBuild)
            return (Change.None, null);

        // Valeur absente, ou prise par ce build : la rendre à la version installée,
        // sinon ne jamais y laisser ce build.
        if (installedValue != null)
            return (Change.Write, installedValue);

        return heldByThisBuild ? (Change.Delete, null) : (Change.None, null);
    }

    /// <summary>
    /// Aligne la valeur de démarrage sur le réglage, selon les règles de <see cref="Decide"/>.
    /// </summary>
    public static void Apply(bool startWithWindows, bool isVelopackCopy)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Constants.StartupRegistryKey, writable: true);
            if (key == null) return;

            var currentValue = key.GetValue(Constants.AppName) as string;
            var ownValue = Quote(GetApplicationExecutablePath());
            var installedValue = isVelopackCopy ? null : Quote(FindInstalledCopy());

            var (change, value) = Decide(startWithWindows, isVelopackCopy, currentValue, ownValue, installedValue);
            switch (change)
            {
                case Change.Write:
                    key.SetValue(Constants.AppName, value!);
                    Log.Info(isVelopackCopy
                        ? $"[Startup] Démarrage avec Windows : {value}"
                        : $"[Startup] Build de dev : démarrage avec Windows rendu à la version installée {value}");
                    break;

                case Change.Delete:
                    key.DeleteValue(Constants.AppName, throwOnMissingValue: false);
                    Log.Info("[Startup] Démarrage avec Windows retiré");
                    break;

                default:
                    if (!isVelopackCopy)
                        Log.Info("[Startup] Build de dev : clé de démarrage laissée intacte");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[Startup] Clé de démarrage non modifiable : {ex.Message}");
        }
    }

    private static bool SameValue(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Quote(string? path) => path != null ? $"\"{path}\"" : null;

    /// <summary>
    /// Exécutable de la version installée par le Setup (dossier par défaut de Velopack,
    /// %LocalAppData%\{packId}\current), ou null s'il n'existe pas.
    /// </summary>
    private static string? FindInstalledCopy()
    {
        var exePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Constants.VelopackPackId, "current", $"{Constants.AppName}.exe");
        return File.Exists(exePath) ? exePath : null;
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
                var exePath = Path.Combine(directory, $"{Constants.AppName}.exe");
                if (File.Exists(exePath))
                    return exePath;
            }
        }

        using var process = Process.GetCurrentProcess();
        var mainModule = process.MainModule?.FileName;
        if (!string.IsNullOrEmpty(mainModule) &&
            mainModule.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !mainModule.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return mainModule;
        }

        return null;
    }
}
