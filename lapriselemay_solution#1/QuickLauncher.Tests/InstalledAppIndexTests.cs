using System.IO;
using Microsoft.Data.Sqlite;
using QuickLauncher.Models;
using QuickLauncher.Services;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>Régression : raccourcis et applications Win32 d'AppsFolder dans le même index.</summary>
public sealed class InstalledAppIndexTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuickLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSettingsProvider _settings = new();
    private readonly FakeStoreAppService _apps = new();
    private readonly FolderFingerprintService _fingerprints;
    private readonly string _shortcut;

    public InstalledAppIndexTests()
    {
        var programs = Path.Combine(_directory, "Programs");
        Directory.CreateDirectory(programs);
        _shortcut = Path.Combine(programs, "GitHub Desktop.lnk");
        File.WriteAllText(_shortcut, "Raccourci simulé par IShortcutHelper");
        _settings.Current.Search.IndexedFolders = [programs];
        _settings.Current.Search.FileExtensions = [".lnk"];
        _fingerprints = new FolderFingerprintService(Path.Combine(_directory, "fingerprints.db"));
    }

    private IndexingService OpenIndex() => new(_settings, _fingerprints, _apps,
        new FakeBookmarkService(), new FakeWindowsSettingsProvider(), new FakeShortcutHelper(),
        new NullLogger(), Path.Combine(_directory, "index.db"));

    private void AssertSingleAppAndBookmark(IndexingService index)
    {
        var results = new SearchService(index, _settings, new CalculatorService()).Search("github");
        Assert.Single(results, r => r.Type == ResultType.Application);
        Assert.Single(results, r => r.Type == ResultType.Bookmark);
        Assert.Equal(2, results.Count);
    }

    [Theory]
    [InlineData("com.squirrel.GitHubDesktop.GitHubDesktop")]
    [InlineData(@"C:\Program Files\GitHub Desktop\GitHubDesktop.exe")]
    public async Task Une_seule_app_apres_indexation_rafraichissement_et_rechargement(string appPath)
    {
        _apps.Path = appPath;
        using (var index = OpenIndex())
        {
            await index.StartIndexingAsync();
            Assert.Equal(2, index.CachedItems.Values.Count(r => r.Type == ResultType.Application));
            AssertSingleAppAndBookmark(index);

            // Le watcher peut réintroduire le raccourci, et le démarrage intelligent
            // rafraîchit AppsFolder même lorsque les dossiers n'ont pas changé.
            index.AddOrUpdateItem(_shortcut);
            await index.SmartStartIndexingAsync();
            Assert.Equal(2, index.CachedItems.Values.Count(r => r.Type == ResultType.Application));
            Assert.True(index.CachedItems[appPath].IsFromAppsFolder);
            index.RecordUsage(index.CachedItems[appPath]);
            AssertSingleAppAndBookmark(index);
        }

        using (var reloaded = OpenIndex())
        {
            Assert.True(reloaded.CachedItems[appPath].IsFromAppsFolder);
            Assert.Equal(1, reloaded.CachedItems[appPath].UseCount);
            AssertSingleAppAndBookmark(reloaded);

            await reloaded.SmartStartIndexingAsync();
            Assert.Equal(1, reloaded.CachedItems[appPath].UseCount);
            AssertSingleAppAndBookmark(reloaded);

            // Une application retirée d'AppsFolder ne doit pas laisser une entrée fantôme.
            _apps.Available = false;
            await reloaded.SmartStartIndexingAsync();
            Assert.DoesNotContain(appPath, reloaded.CachedItems.Keys);
            AssertSingleAppAndBookmark(reloaded);
        }
    }

    [Fact]
    public async Task Le_demarrage_identifie_les_entrees_Win32_de_l_ancien_index()
    {
        using (var index = OpenIndex())
        {
            await index.StartIndexingAsync();
            index.AddOrUpdateItem(_shortcut);
            await index.SmartStartIndexingAsync();
            index.RecordUsage(index.CachedItems[_apps.Path]);
        }

        // Simuler les métadonnées de l'ancien index, qui ignorait la provenance.
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(_directory, "index.db")}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Items SET IsFromAppsFolder = 0";
            cmd.ExecuteNonQuery();
        }

        using var reloaded = OpenIndex();
        var search = new SearchService(reloaded, _settings, new CalculatorService());
        Assert.Equal(2, search.Search("github").Count(r => r.Type == ResultType.Application));
        await reloaded.SmartStartIndexingAsync();
        AssertSingleAppAndBookmark(reloaded);
        Assert.True(reloaded.CachedItems[_apps.Path].IsFromAppsFolder);
        Assert.Equal(1, reloaded.CachedItems[_apps.Path].UseCount);
    }

    [Fact]
    public async Task Le_scan_et_le_watcher_du_meme_executable_conservent_son_identite_AppsFolder()
    {
        // Le nom du fichier diffère volontairement du nom affiché par Windows.
        var executable = Path.Combine(Path.GetDirectoryName(_shortcut)!, "GitHubDesktop.exe");
        File.WriteAllText(executable, "Exécutable simulé, jamais lancé");
        _settings.Current.Search.FileExtensions.Add(".exe");
        _apps.Path = executable;

        using (var index = OpenIndex())
        {
            await index.StartIndexingAsync();
            Assert.True(index.CachedItems[executable].IsFromAppsFolder);
            Assert.Equal("GitHub Desktop", index.CachedItems[executable].Name);
            AssertSingleAppAndBookmark(index);

            index.RecordUsage(index.CachedItems[executable]);
            index.AddOrUpdateItem(executable);
            Assert.True(index.CachedItems[executable].IsFromAppsFolder);
            Assert.Equal("GitHub Desktop", index.CachedItems[executable].Name);
            AssertSingleAppAndBookmark(index);

            await index.StartIndexingAsync();
            AssertSingleAppAndBookmark(index);
            Assert.Equal(1, index.CachedItems[executable].UseCount);
        }

        using var reloaded = OpenIndex();
        Assert.True(reloaded.CachedItems[executable].IsFromAppsFolder);
        Assert.Equal("GitHub Desktop", reloaded.CachedItems[executable].Name);
        AssertSingleAppAndBookmark(reloaded);

        // Une passe complète doit utiliser les métadonnées fraîches, pas le cache.
        _apps.Name = "GitHub Desktop Beta";
        await reloaded.StartIndexingAsync();
        Assert.Equal(_apps.Name, reloaded.CachedItems[executable].Name);
        Assert.True(reloaded.CachedItems[executable].IsFromAppsFolder);
        Assert.Equal(1, reloaded.CachedItems[executable].UseCount);

        _apps.Available = false;
        await reloaded.StartIndexingAsync();
        Assert.False(reloaded.CachedItems[executable].IsFromAppsFolder);
        Assert.Equal("GitHubDesktop", reloaded.CachedItems[executable].Name);
    }

    [Fact]
    public async Task Le_scan_partiel_conserve_les_raccourcis_sources_et_leur_usage()
    {
        var otherFolder = Path.Combine(Path.GetDirectoryName(_shortcut)!, "Autre");
        Directory.CreateDirectory(otherFolder);
        var otherShortcut = Path.Combine(otherFolder, "GitHub Desktop.lnk");
        File.Copy(_shortcut, otherShortcut);

        using var index = OpenIndex();
        await index.StartIndexingAsync();
        index.RecordUsage(index.CachedItems[otherShortcut]);
        File.AppendAllText(_shortcut, " modifié");
        await index.SmartStartIndexingAsync();

        Assert.Contains(_shortcut, index.CachedItems.Keys);
        Assert.Equal(1, index.CachedItems[otherShortcut].UseCount);
        AssertSingleAppAndBookmark(index);
    }

    public void Dispose()
    {
        _fingerprints.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeSettingsProvider : ISettingsProvider
    {
        public AppSettings Current { get; } = new();
        public event EventHandler<AppSettings>? SettingsChanged { add { } remove { } }
        public bool Save() => true;
        public void Reload() { }
        public bool Update(Action<AppSettings> update) { update(Current); return true; }
    }

    private sealed class FakeStoreAppService : IStoreAppService
    {
        public string Name { get; set; } = "GitHub Desktop";
        public string Path { get; set; } = "com.squirrel.GitHubDesktop.GitHubDesktop";
        public bool Available { get; set; } = true;
        public List<SearchResult> GetAllApps() => Available ? [new()
        {
            Name = Name, Path = Path, Description = "Application",
            Type = ResultType.Application, IsFromAppsFolder = true
        }] : [];
        public bool LaunchApp(string appUserModelId) => throw new NotSupportedException();
    }

    private sealed class FakeBookmarkService : IBookmarkService
    {
        public List<SearchResult> GetAllBookmarks() => [new()
        {
            Name = "GitHub", Path = "https://github.com/", Type = ResultType.Bookmark
        }];
        public List<BrowserInfo> GetSupportedBrowsers() => [];
        public List<SearchResult> GetBookmarksForBrowser(string browserName) => [];
    }

    private sealed class FakeWindowsSettingsProvider : IWindowsSettingsProvider
    {
        public List<SearchResult> GetItems() => [];
    }

    private sealed class FakeShortcutHelper : IShortcutHelper
    {
        public ShortcutInfo? ResolveShortcut(string shortcutPath) => new()
        {
            TargetPath = @"C:\Program Files\GitHub Desktop\GitHubDesktop.exe",
            Description = "Simple collaboration from your desktop"
        };
    }

    private sealed class NullLogger : ILogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
