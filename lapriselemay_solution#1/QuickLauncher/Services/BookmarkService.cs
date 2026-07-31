using System.IO;
using System.Text.Json;
using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Information sur un navigateur supporté.
/// </summary>
public class BrowserInfo
{
    public string Name { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public bool IsInstalled { get; init; }
    public int BookmarkCount { get; set; }
}

/// <summary>
/// Abstraction pour le service d'indexation des favoris navigateurs.
/// Permet l'injection de dépendances et la testabilité (Amélioration #2).
/// </summary>
public interface IBookmarkService
{
    List<SearchResult> GetAllBookmarks();
    List<BrowserInfo> GetSupportedBrowsers();
    List<SearchResult> GetBookmarksForBrowser(string browserName);
}

/// <summary>
/// Service pour indexer les favoris des navigateurs (Chrome, Edge, Firefox, Brave, Vivaldi, Opera).
/// Converti de static vers injectable (Amélioration #2).
/// </summary>
public class BookmarkService : IBookmarkService
{
    #region Browser Paths
    
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    
    // Dossiers "User Data" des navigateurs Chromium
    private static readonly string ChromeUserDataPath = Path.Combine(LocalAppData, "Google", "Chrome", "User Data");
    private static readonly string EdgeUserDataPath = Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data");
    private static readonly string BraveUserDataPath = Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data");
    private static readonly string VivaldiUserDataPath = Path.Combine(LocalAppData, "Vivaldi", "User Data");
    private static readonly string OperaUserDataPath = Path.Combine(RoamingAppData, "Opera Software", "Opera Stable");
    private static readonly string OperaGXUserDataPath = Path.Combine(RoamingAppData, "Opera Software", "Opera GX Stable");
    
    // Firefox (utilise un profil avec nom aléatoire)
    private static readonly string FirefoxProfilesPath = Path.Combine(RoamingAppData, "Mozilla", "Firefox", "Profiles");
    
    #endregion
    
    #region Browser Detection
    
    /// <summary>
    /// Trouve tous les fichiers Bookmarks dans un dossier User Data de navigateur Chromium.
    /// Cherche dans Default, Profile 1, Profile 2, etc.
    /// </summary>
    private static List<string> FindChromiumBookmarkFiles(string userDataPath)
    {
        var bookmarkFiles = new List<string>();
        
        if (!Directory.Exists(userDataPath))
            return bookmarkFiles;
        
        try
        {
            // Profils à chercher: Default, Profile 1, Profile 2, etc.
            var profileDirs = Directory.GetDirectories(userDataPath)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return name == "Default" || name.StartsWith("Profile ");
                });
            
            foreach (var profileDir in profileDirs)
            {
                var bookmarksPath = Path.Combine(profileDir, "Bookmarks");
                if (File.Exists(bookmarksPath))
                    bookmarkFiles.Add(bookmarksPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BookmarkService] Erreur énumération profils '{userDataPath}': {ex.Message}");
        }

        return bookmarkFiles;
    }
    
    /// <summary>
    /// Vérifie si un navigateur Chromium a des favoris dans au moins un profil.
    /// </summary>
    private static bool HasChromiumBookmarks(string userDataPath)
    {
        return FindChromiumBookmarkFiles(userDataPath).Count > 0;
    }
    
    /// <summary>
    /// Récupère tous les favoris de tous les profils d'un navigateur Chromium.
    /// </summary>
    private static List<SearchResult> GetAllChromiumBookmarks(string userDataPath, string browserName)
    {
        var bookmarks = new List<SearchResult>();
        
        foreach (var bookmarkFile in FindChromiumBookmarkFiles(userDataPath))
        {
            bookmarks.AddRange(GetChromiumBookmarks(bookmarkFile, browserName));
        }
        
        return bookmarks;
    }
    
    /// <summary>
    /// Retourne la liste des navigateurs supportés avec leur statut d'installation.
    /// </summary>
    public List<BrowserInfo> GetSupportedBrowsers()
    {
        return
        [
            new BrowserInfo 
            { 
                Name = "Chrome", 
                Icon = "🌐", 
                IsInstalled = HasChromiumBookmarks(ChromeUserDataPath),
                BookmarkCount = GetAllChromiumBookmarks(ChromeUserDataPath, "Chrome").Count
            },
            new BrowserInfo 
            { 
                Name = "Edge", 
                Icon = "🔷", 
                IsInstalled = HasChromiumBookmarks(EdgeUserDataPath),
                BookmarkCount = GetAllChromiumBookmarks(EdgeUserDataPath, "Edge").Count
            },
            new BrowserInfo 
            { 
                Name = "Firefox", 
                Icon = "🦊", 
                IsInstalled = Directory.Exists(FirefoxProfilesPath) && Directory.GetDirectories(FirefoxProfilesPath).Length > 0,
                BookmarkCount = GetFirefoxBookmarks().Count
            },
            new BrowserInfo 
            { 
                Name = "Brave", 
                Icon = "🦁", 
                IsInstalled = HasChromiumBookmarks(BraveUserDataPath),
                BookmarkCount = GetAllChromiumBookmarks(BraveUserDataPath, "Brave").Count
            },
            new BrowserInfo 
            { 
                Name = "Vivaldi", 
                Icon = "🎵", 
                IsInstalled = HasChromiumBookmarks(VivaldiUserDataPath),
                BookmarkCount = GetAllChromiumBookmarks(VivaldiUserDataPath, "Vivaldi").Count
            },
            new BrowserInfo 
            { 
                Name = "Opera", 
                Icon = "🔴", 
                IsInstalled = HasChromiumBookmarks(OperaUserDataPath),
                BookmarkCount = GetAllChromiumBookmarks(OperaUserDataPath, "Opera").Count
            },
            new BrowserInfo 
            { 
                Name = "Opera GX", 
                Icon = "🎮", 
                IsInstalled = HasChromiumBookmarks(OperaGXUserDataPath),
                BookmarkCount = GetAllChromiumBookmarks(OperaGXUserDataPath, "Opera GX").Count
            }
        ];
    }
    
    /// <summary>
    /// Importe les favoris d'un navigateur spécifique.
    /// </summary>
    public List<SearchResult> GetBookmarksForBrowser(string browserName)
    {
        return browserName switch
        {
            "Chrome" => GetAllChromiumBookmarks(ChromeUserDataPath, "Chrome"),
            "Edge" => GetAllChromiumBookmarks(EdgeUserDataPath, "Edge"),
            "Firefox" => GetFirefoxBookmarks(),
            "Brave" => GetAllChromiumBookmarks(BraveUserDataPath, "Brave"),
            "Vivaldi" => GetAllChromiumBookmarks(VivaldiUserDataPath, "Vivaldi"),
            "Opera" => GetAllChromiumBookmarks(OperaUserDataPath, "Opera"),
            "Opera GX" => GetAllChromiumBookmarks(OperaGXUserDataPath, "Opera GX"),
            _ => []
        };
    }
    
    #endregion
    
    /// <summary>
    /// Récupère tous les favoris de tous les navigateurs installés.
    /// </summary>
    public List<SearchResult> GetAllBookmarks()
    {
        var bookmarks = new List<SearchResult>();
        
        // Chrome
        bookmarks.AddRange(GetAllChromiumBookmarks(ChromeUserDataPath, "Chrome"));
        
        // Edge
        bookmarks.AddRange(GetAllChromiumBookmarks(EdgeUserDataPath, "Edge"));
        
        // Brave
        bookmarks.AddRange(GetAllChromiumBookmarks(BraveUserDataPath, "Brave"));
        
        // Vivaldi
        bookmarks.AddRange(GetAllChromiumBookmarks(VivaldiUserDataPath, "Vivaldi"));
        
        // Opera
        bookmarks.AddRange(GetAllChromiumBookmarks(OperaUserDataPath, "Opera"));
        
        // Opera GX
        bookmarks.AddRange(GetAllChromiumBookmarks(OperaGXUserDataPath, "Opera GX"));
        
        // Firefox
        bookmarks.AddRange(GetFirefoxBookmarks());
        
        // Dédupliquer par URL
        return bookmarks
            .GroupBy(b => b.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
    
    #region Chromium-based browsers (Chrome, Edge, Brave, Vivaldi)
    
    /// <summary>
    /// Lit les favoris d'un navigateur basé sur Chromium.
    /// Le format est JSON avec une structure roots > bookmark_bar/other/synced.
    /// </summary>
    private static List<SearchResult> GetChromiumBookmarks(string bookmarksPath, string browserName)
    {
        var results = new List<SearchResult>();
        
        if (!File.Exists(bookmarksPath))
            return results;
        
        try
        {
            var json = File.ReadAllText(bookmarksPath);
            using var doc = JsonDocument.Parse(json);
            
            var roots = doc.RootElement.GetProperty("roots");
            
            // Parcourir les différentes sections de favoris
            if (roots.TryGetProperty("bookmark_bar", out var bookmarkBar))
                ParseChromiumFolder(bookmarkBar, results, browserName, "Barre de favoris");
            
            if (roots.TryGetProperty("other", out var other))
                ParseChromiumFolder(other, results, browserName, "Autres favoris");
            
            if (roots.TryGetProperty("synced", out var synced))
                ParseChromiumFolder(synced, results, browserName, "Favoris synchronisés");
        }
        catch
        {
            // Silently fail - le fichier peut être verrouillé ou corrompu
        }
        
        return results;
    }
    
    private static void ParseChromiumFolder(JsonElement element, List<SearchResult> results, 
        string browserName, string folderPath)
    {
        if (!element.TryGetProperty("children", out var children))
            return;
        
        foreach (var child in children.EnumerateArray())
        {
            if (!child.TryGetProperty("type", out var typeElement))
                continue;
            
            var type = typeElement.GetString();
            
            if (type == "url")
            {
                // C'est un favori
                var name = child.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = child.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url) && IsValidUrl(url))
                {
                    results.Add(new SearchResult
                    {
                        Name = name,
                        Path = url,
                        Description = $"{browserName} • {folderPath}",
                        Type = ResultType.Bookmark
                    });
                }
            }
            else if (type == "folder")
            {
                // C'est un dossier, parcourir récursivement
                var folderName = child.TryGetProperty("name", out var fn) ? fn.GetString() ?? "" : "";
                var newPath = string.IsNullOrEmpty(folderName) ? folderPath : $"{folderPath}/{folderName}";
                ParseChromiumFolder(child, results, browserName, newPath);
            }
        }
    }
    
    #endregion
    
    #region Firefox
    
    /// <summary>
    /// Lit les favoris de Firefox.
    /// Firefox stocke les favoris dans une base SQLite (places.sqlite).
    /// </summary>
    private static List<SearchResult> GetFirefoxBookmarks()
    {
        var results = new List<SearchResult>();
        
        if (!Directory.Exists(FirefoxProfilesPath))
            return results;
        
        try
        {
            // Trouver tous les profils Firefox
            var profiles = Directory.GetDirectories(FirefoxProfilesPath);
            
            foreach (var profile in profiles)
            {
                var placesDb = Path.Combine(profile, "places.sqlite");
                if (!File.Exists(placesDb))
                    continue;
                
                // Firefox verrouille le fichier, on doit le copier
                var tempDb = Path.Combine(Path.GetTempPath(), $"firefox_bookmarks_{Guid.NewGuid()}.sqlite");
                
                try
                {
                    File.Copy(placesDb, tempDb, overwrite: true);
                    results.AddRange(ReadFirefoxDatabase(tempDb));
                }
                finally
                {
                    try { File.Delete(tempDb); } catch { }
                }
            }
        }
        catch
        {
            // Silently fail
        }
        
        return results;
    }
    
    private static List<SearchResult> ReadFirefoxDatabase(string dbPath)
    {
        var results = new List<SearchResult>();
        
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT b.title, p.url, parent.title as folder
                FROM moz_bookmarks b
                JOIN moz_places p ON b.fk = p.id
                LEFT JOIN moz_bookmarks parent ON b.parent = parent.id
                WHERE b.type = 1 
                  AND p.url IS NOT NULL 
                  AND p.url NOT LIKE 'place:%'
                  AND b.title IS NOT NULL
                  AND b.title != ''
                """;
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var url = reader.GetString(1);
                var folder = reader.IsDBNull(2) ? "" : reader.GetString(2);
                
                if (IsValidUrl(url))
                {
                    var description = string.IsNullOrEmpty(folder) 
                        ? "Firefox" 
                        : $"Firefox • {folder}";
                    
                    results.Add(new SearchResult
                    {
                        Name = name,
                        Path = url,
                        Description = description,
                        Type = ResultType.Bookmark
                    });
                }
            }
        }
        catch
        {
            // Silently fail - la base peut être corrompue ou incompatible
        }
        
        return results;
    }
    
    #endregion
    
    #region Helpers
    
    /// <summary>
    /// Vérifie si l'URL est valide (http/https).
    /// </summary>
    private static bool IsValidUrl(string url)
    {
        return !string.IsNullOrEmpty(url) && 
               (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }
    
    /// <summary>
    /// Extrait le domaine d'une URL pour l'affichage.
    /// </summary>
    public static string GetDomain(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            return url;
        }
    }
    
    #endregion
}
