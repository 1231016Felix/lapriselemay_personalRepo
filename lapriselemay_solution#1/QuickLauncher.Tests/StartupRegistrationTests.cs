using System.IO;
using QuickLauncher.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

using Change = QuickLauncher.Services.StartupRegistration.Change;

namespace QuickLauncher.Tests;

/// <summary>
/// Démarrage avec Windows : la clé Run désigne toujours une copie Velopack (installée ou
/// portable), jamais une sortie de build. Un build de dev qui y inscrivait son chemin était
/// relancé à chaque session et verrouillait bin\Release au build suivant (MSB3027).
/// </summary>
public class StartupRegistrationTests
{
    private const string Installed = @"""C:\Users\u\AppData\Local\LapriseLemay.QuickLauncher\current\QuickLauncher.exe""";
    private const string Portable = @"""D:\Outils\QuickLauncher\current\QuickLauncher.exe""";
    private const string DevBuild = @"""C:\git\repo\QuickLauncher\bin\Release\net10.0-windows10.0.19041.0\QuickLauncher.exe""";
    private const string OtherDevBuild = @"""C:\git\repo\QuickLauncher\bin\Debug\net10.0-windows10.0.19041.0\QuickLauncher.exe""";

    // --- Copie Velopack : elle s'inscrit elle-même ---

    [Theory]
    [InlineData(null)]
    [InlineData(DevBuild)] // clé prise par un ancien build de dev
    [InlineData(Portable)]
    public void Copie_Velopack_inscrit_son_chemin(string? currentValue)
    {
        var (change, value) = StartupRegistration.Decide(true, isVelopackCopy: true, currentValue, Installed, installedValue: null);
        Assert.Equal(Change.Write, change);
        Assert.Equal(Installed, value);
    }

    [Theory]
    [InlineData(Installed)]
    [InlineData(@"""C:\USERS\U\APPDATA\LOCAL\LAPRISELEMAY.QUICKLAUNCHER\CURRENT\QUICKLAUNCHER.EXE""")]
    public void Copie_Velopack_deja_inscrite_ne_reecrit_rien(string currentValue)
    {
        var (change, _) = StartupRegistration.Decide(true, isVelopackCopy: true, currentValue, Installed, installedValue: null);
        Assert.Equal(Change.None, change);
    }

    [Fact]
    public void Chemin_inconnu_ne_reecrit_rien()
    {
        var (change, _) = StartupRegistration.Decide(true, isVelopackCopy: true, DevBuild, ownValue: null, installedValue: null);
        Assert.Equal(Change.None, change);
    }

    // --- Build de dev : il ne s'inscrit jamais lui-même ---

    [Theory]
    [InlineData(Installed)]
    [InlineData(Portable)]
    [InlineData(OtherDevBuild)]
    public void Build_de_dev_ne_touche_pas_a_une_autre_copie(string currentValue)
    {
        var (change, _) = StartupRegistration.Decide(true, isVelopackCopy: false, currentValue, DevBuild, Installed);
        Assert.Equal(Change.None, change);
    }

    [Fact]
    public void Build_de_dev_rend_sa_cle_a_la_version_installee()
    {
        // État laissé par l'ancien bug : la version installée ne peut pas reprendre la clé
        // elle-même tant que ce build tourne, l'instance unique l'arrête avant OnStartup.
        var (change, value) = StartupRegistration.Decide(true, isVelopackCopy: false, DevBuild.ToLowerInvariant(), DevBuild, Installed);
        Assert.Equal(Change.Write, change);
        Assert.Equal(Installed, value);
    }

    [Fact]
    public void Build_de_dev_retire_sa_cle_sans_version_installee()
    {
        var (change, _) = StartupRegistration.Decide(true, isVelopackCopy: false, DevBuild, DevBuild, installedValue: null);
        Assert.Equal(Change.Delete, change);
    }

    [Fact]
    public void Build_de_dev_reinscrit_la_version_installee_si_la_cle_manque()
    {
        var (change, value) = StartupRegistration.Decide(true, isVelopackCopy: false, currentValue: null, DevBuild, Installed);
        Assert.Equal(Change.Write, change);
        Assert.Equal(Installed, value);
    }

    [Fact]
    public void Build_de_dev_sans_version_installee_ne_cree_rien()
    {
        var (change, _) = StartupRegistration.Decide(true, isVelopackCopy: false, currentValue: null, DevBuild, installedValue: null);
        Assert.Equal(Change.None, change);
    }

    [Fact]
    public void Build_de_dev_n_ecrit_jamais_son_propre_chemin()
    {
        string?[] currentValues = [null, Installed, Portable, OtherDevBuild, DevBuild, DevBuild.ToUpperInvariant()];
        string?[] installedValues = [null, Installed];

        foreach (var startWithWindows in new[] { true, false })
        foreach (var currentValue in currentValues)
        foreach (var installedValue in installedValues)
        {
            var (change, value) = StartupRegistration.Decide(startWithWindows, isVelopackCopy: false, currentValue, DevBuild, installedValue);
            Assert.False(
                change == Change.Write && string.Equals(value, DevBuild, StringComparison.OrdinalIgnoreCase),
                $"start={startWithWindows}, current={currentValue ?? "(absente)"}, installed={installedValue ?? "(absente)"}");
        }
    }

    // --- Option décochée : n'importe quelle copie retire la valeur ---

    [Theory]
    [InlineData(true, Installed)]
    [InlineData(false, Installed)]
    [InlineData(false, DevBuild)]
    public void Demarrage_desactive_retire_la_valeur(bool isVelopackCopy, string currentValue)
    {
        var (change, _) = StartupRegistration.Decide(false, isVelopackCopy, currentValue, DevBuild, Installed);
        Assert.Equal(Change.Delete, change);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Demarrage_desactive_sans_valeur_ne_fait_rien(bool isVelopackCopy)
    {
        var (change, _) = StartupRegistration.Decide(false, isVelopackCopy, currentValue: null, DevBuild, Installed);
        Assert.Equal(Change.None, change);
    }

    // --- Contrat Velopack : la règle s'appuie sur UpdateManager.IsInstalled (UpdateService.IsInstalled) ---

    [Fact]
    public void Velopack_reconnait_une_copie_installee()
    {
        using var layout = TempLayout.VelopackCopy(portable: false);
        var (isInstalled, isPortable) = Probe(layout.ExePath);
        Assert.True(isInstalled);
        Assert.False(isPortable);
    }

    [Fact]
    public void Velopack_reconnait_une_copie_portable()
    {
        using var layout = TempLayout.VelopackCopy(portable: true);
        var (isInstalled, isPortable) = Probe(layout.ExePath);
        Assert.True(isInstalled);
        Assert.True(isPortable);
    }

    [Fact]
    public void Velopack_ne_reconnait_pas_un_build_de_dev()
    {
        using var layout = TempLayout.DevBuild();
        Assert.False(Probe(layout.ExePath).IsInstalled);
    }

    /// <summary>Même construction qu'<see cref="UpdateService"/>, avec un processus simulé.</summary>
    private static (bool IsInstalled, bool IsPortable) Probe(string exePath)
    {
        var locator = new WindowsVelopackLocator(new FakeProcess(exePath), null);
        var manager = new UpdateManager(new GithubSource(Constants.UpdateRepositoryUrl, null, prerelease: false), null, locator);
        return (manager.IsInstalled, manager.IsPortable);
    }

    private sealed class FakeProcess(string path) : IProcessImpl
    {
        public string GetCurrentProcessPath() => path;
        public uint GetCurrentProcessId() => 4242;
        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow) =>
            throw new NotSupportedException();
        public void Exit(int exitCode) => throw new NotSupportedException();
    }

    /// <summary>Arborescence temporaire imitant une copie de QuickLauncher.</summary>
    private sealed class TempLayout : IDisposable
    {
        // Manifeste au format réel de Velopack (current\sq.version). L'id est propre aux
        // tests : le locator journalise dans %LocalAppData%\velopack\velopack_{id}.log, et
        // l'id réel (LapriseLemay.QuickLauncher) mélangerait ces lignes au journal de l'app
        // installée. La détection (Update.exe, sq.version, .portable) ne dépend pas de l'id.
        private const string Manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
            <metadata>
            <id>QuickLauncher.Tests.VelopackProbe</id>
            <title>QuickLauncher</title>
            <description>QuickLauncher</description>
            <authors>Felix-Antoine Laprise-Lemay</authors>
            <version>1.3.0</version>
            <channel>win</channel>
            <mainExe>QuickLauncher.exe</mainExe>
            <os>win</os>
            <rid>win</rid>
            </metadata>
            </package>
            """;

        private readonly string _root;

        public string ExePath { get; }

        private TempLayout(string root, string exePath)
        {
            _root = root;
            ExePath = exePath;
        }

        /// <summary>Copie Velopack : Update.exe à la racine, l'app et son manifeste dans current\.</summary>
        public static TempLayout VelopackCopy(bool portable)
        {
            var root = NewRoot();
            var current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
            File.WriteAllBytes(Path.Combine(root, "Update.exe"), []);
            File.WriteAllText(Path.Combine(current, "sq.version"), Manifest);
            if (portable)
                File.WriteAllBytes(Path.Combine(root, ".portable"), []);

            var exe = Path.Combine(current, "QuickLauncher.exe");
            File.WriteAllBytes(exe, []);
            return new TempLayout(root, exe);
        }

        /// <summary>Sortie de build Visual Studio : aucun Update.exe au-dessus de l'exe.</summary>
        public static TempLayout DevBuild()
        {
            var root = NewRoot();
            var bin = Directory.CreateDirectory(
                Path.Combine(root, "QuickLauncher", "bin", "Release", "net10.0-windows10.0.19041.0")).FullName;

            var exe = Path.Combine(bin, "QuickLauncher.exe");
            File.WriteAllBytes(exe, []);
            return new TempLayout(root, exe);
        }

        private static string NewRoot() =>
            Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "QuickLauncher.Tests", Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* best-effort : le dossier temporaire sera purgé par Windows */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
