using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Service de recherche universel qui fonctionne sans Windows Search.
/// Utilise Everything si disponible, sinon fait une recherche directe.
/// </summary>
public class UniversalSearchService : IUniversalSearchService
{
    private const int MaxResultsLimit = 50;
    private readonly string[] _defaultSearchPaths;
    private readonly IShortcutHelper _shortcutHelper;
    private static readonly string[] ExcludedFolders =
    [
        "node_modules", ".git", ".vs", "bin", "obj", "__pycache__",
        "AppData\\Local\\Temp", "Windows\\Temp", "$Recycle.Bin",
        "Windows\\WinSxS", "Windows\\assembly", ".nuget", "packages"
    ];

    /// <summary>Exclusions découpées en segments (« AppData\Local\Temp » → 3 segments).</summary>
    private static readonly string[][] ExcludedFolderSegments =
        ExcludedFolders.Select(e => e.Split('\\', StringSplitOptions.RemoveEmptyEntries)).ToArray();

    /// <summary>
    /// Plafond de dossiers visités par une recherche directe : sans lui, une arborescence
    /// géante sans correspondance pouvait être parcourue longtemps sans rien produire.
    /// </summary>
    private const int MaxDirectoriesPerSearch = 20_000;

    private readonly ConcurrentDictionary<string, CachedSearchResult> _searchCache = new();
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(60);
    private const int MaxCacheEntries = 20;
    /// <inheritdoc/>
    public int MaxSearchDepth { get; set; } = 5;

    /// <inheritdoc/>
    public string[] GetDefaultSearchPaths() => _defaultSearchPaths;

    public UniversalSearchService(IShortcutHelper shortcutHelper)
    {
        _shortcutHelper = shortcutHelper;
        _defaultSearchPaths =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            @"C:\Program Files",
            @"C:\Program Files (x86)"
        ];
    }

    /// <inheritdoc/>
    public async Task<List<SearchResult>> SearchAsync(
        string query,
        string? searchScope = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        List<SearchResult>? results = null;

        if (EverythingApi.IsAvailable())
        {
            try
            {
                results = await Task.Run(() => SearchWithEverything(query, MaxResultsLimit), cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UniversalSearch] Everything failed: {ex.Message}");
            }
        }
        if (results == null || results.Count == 0)
        {
            if (WindowsSearchService.IsAvailable())
            {
                try
                {
                    results = await WindowsSearchService.SearchAsync(query, searchScope, cancellationToken);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UniversalSearch] Windows Search failed: {ex.Message}");
                }
            }
        }

        if (results == null || results.Count == 0)
            results = await SearchDirectWithCacheAsync(query, searchScope, cancellationToken);

        return DeduplicateResults(results);
    }

    /// <summary>
    /// Déduplique les résultats de recherche par identité réelle.
    /// Pour les raccourcis .lnk, on résout la cible pour éviter d'afficher
    /// le même programme plusieurs fois (ex: Ollama.lnk dans Start Menu, StartUp, Programs\Ollama).
    /// Les fichiers homonymes de dossiers différents ne sont jamais fusionnés.
    /// Conserve le résultat ayant le meilleur score, puis le plus récent.
    /// </summary>
    private List<SearchResult> DeduplicateResults(List<SearchResult> results)
    {
        if (results.Count <= 1)
            return results;

        return results
            .GroupBy(r => GetDeduplicationKey(r), StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.Type == ResultType.Application ? 1 : 0)
                .ThenBy(r => r.Path.Length) // Préférer le chemin le plus court (plus canonique)
                .First())
            .ToList();
    }

    /// <summary>
    /// Génère une clé de déduplication pour un résultat.
    /// Pour les .lnk, on utilise le chemin cible COMPLET (résolu) afin de regrouper
    /// les raccourcis pointant vers le même exécutable — mais pas deux « setup.exe » différents.
    /// Pour les autres fichiers, la clé est le chemin complet.
    /// </summary>
    private string GetDeduplicationKey(SearchResult result)
    {
        var path = result.Path;

        // Pour les raccourcis .lnk, résoudre la cible
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var info = _shortcutHelper.ResolveShortcut(path);
                if (info != null && !string.IsNullOrEmpty(info.TargetPath))
                    return DeduplicationHelper.NormalizePath(info.TargetPath);
            }
            catch { /* Fallback au nom */ }
        }

        // Pour les fichiers normaux, clé = chemin complet : deux « budget.xlsx » de deux
        // dossiers clients restent deux résultats ; seul un même fichier renvoyé deux fois
        // (par deux moteurs, par exemple) est fusionné.
        return DeduplicationHelper.NormalizePath(path);
    }

    /// <inheritdoc/>
    public SearchEngineStatus GetAvailableEngine()
    {
        if (EverythingApi.IsAvailable())
            return SearchEngineStatus.Everything;
        if (WindowsSearchService.IsAvailable())
            return SearchEngineStatus.WindowsSearch;
        return SearchEngineStatus.DirectSearch;
    }

    /// <inheritdoc/>
    public void RefreshEngineDetection()
    {
        EverythingApi.RefreshAvailability();
        WindowsSearchService.RefreshAvailability();
    }
    /// <inheritdoc/>
    public SearchEngineInfo GetEngineInfo(bool forceRefresh = false)
    {
        if (forceRefresh)
            RefreshEngineDetection();

        var status = GetAvailableEngine();

        return status switch
        {
            SearchEngineStatus.Everything => new SearchEngineInfo
            {
                Status = status,
                Name = "Everything",
                Description = "Recherche ultra-rapide via Everything",
                IsOptimal = true,
                Icon = "⚡",
                Recommendation = null
            },
            SearchEngineStatus.WindowsSearch => new SearchEngineInfo
            {
                Status = status,
                Name = "Windows Search",
                Description = "Service d'indexation Windows actif",
                IsOptimal = false,
                Icon = "🔍",
                Recommendation = "Installez Everything pour de meilleures performances"
            },            _ => new SearchEngineInfo
            {
                Status = status,
                Name = "Recherche directe",
                Description = "Scan du système de fichiers (plus lent)",
                IsOptimal = false,
                Icon = "🐢",
                Recommendation = "Installez Everything ou activez le service Windows Search"
            }
        };
    }

    /// <inheritdoc/>
    public void ClearCache()
    {
        _searchCache.Clear();
        Debug.WriteLine("[UniversalSearch] Cache cleared");
    }

    /// <inheritdoc/>
    public (int EntryCount, int TotalResults) GetCacheStats()
    {
        var totalResults = _searchCache.Values.Sum(c => c.Results.Count);
        return (_searchCache.Count, totalResults);
    }
    #region Everything Search

    private static List<SearchResult> SearchWithEverything(string query, int maxResults)
    {
        var results = new List<SearchResult>();

        EverythingApi.Reset();
        EverythingApi.SetSearch(query);
        EverythingApi.SetMax(maxResults);
        EverythingApi.SetRequestFlags(
            EverythingApi.EVERYTHING_REQUEST_FILE_NAME |
            EverythingApi.EVERYTHING_REQUEST_PATH |
            EverythingApi.EVERYTHING_REQUEST_SIZE |
            EverythingApi.EVERYTHING_REQUEST_DATE_MODIFIED);

        EverythingApi.Query(true);

        var numResults = EverythingApi.GetNumResults();
        for (uint i = 0; i < numResults && results.Count < maxResults; i++)
        {
            var path = EverythingApi.GetResultFullPathName(i);
            if (string.IsNullOrEmpty(path)) continue;

            var name = Path.GetFileName(path);
            var isFolder = EverythingApi.IsFolder(i);
            var size = EverythingApi.GetResultSize(i);
            var modified = EverythingApi.GetResultDateModified(i);
            results.Add(new SearchResult
            {
                Name = isFolder ? name : Path.GetFileNameWithoutExtension(path),
                Path = path,
                Description = BuildDescription(path, isFolder ? null : size, modified),
                Type = DetermineResultType(path, isFolder),
                Score = 500
            });
        }

        return results;
    }

    #endregion

    #region Direct Filesystem Search with Cache

    private async Task<List<SearchResult>> SearchDirectWithCacheAsync(
        string query, string? searchScope, CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(query, searchScope);

        if (_searchCache.TryGetValue(cacheKey, out var cached))
        {
            if (DateTime.UtcNow - cached.Timestamp < _cacheExpiration)
            {
                Debug.WriteLine($"[UniversalSearch] Cache hit for '{query}' ({cached.Results.Count} results)");
                return cached.Results.ToList();
            }
            _searchCache.TryRemove(cacheKey, out _);
        }
        var results = await SearchDirectAsync(query, searchScope, cancellationToken);

        if (!cancellationToken.IsCancellationRequested && results.Count > 0)
        {
            CleanupCacheIfNeeded();
            _searchCache[cacheKey] = new CachedSearchResult
            {
                Query = query,
                Results = results,
                Timestamp = DateTime.UtcNow
            };
            Debug.WriteLine($"[UniversalSearch] Cached '{query}' ({results.Count} results)");
        }

        return results;
    }

    private static string BuildCacheKey(string query, string? scope)
    {
        var normalizedQuery = query.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(scope)
            ? normalizedQuery
            : $"{normalizedQuery}|{scope.ToLowerInvariant()}";
    }

    private void CleanupCacheIfNeeded()
    {
        if (_searchCache.Count < MaxCacheEntries) return;
        var expiredKeys = _searchCache
            .Where(kv => DateTime.UtcNow - kv.Value.Timestamp > _cacheExpiration)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expiredKeys)
            _searchCache.TryRemove(key, out _);

        if (_searchCache.Count >= MaxCacheEntries)
        {
            var oldestKeys = _searchCache
                .OrderBy(kv => kv.Value.Timestamp)
                .Take(_searchCache.Count - MaxCacheEntries + 5)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldestKeys)
                _searchCache.TryRemove(key, out _);
        }
    }

    private async Task<List<SearchResult>> SearchDirectAsync(
        string query, string? searchScope, CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();
        var searchPaths = string.IsNullOrEmpty(searchScope)
            ? _defaultSearchPaths.Where(Directory.Exists).ToArray()
            : [searchScope];
        var pattern = $"*{query}*";

        // Énumération d'UN niveau à la fois : le parcours est piloté ici, ce qui permet de
        // filtrer les dossiers exclus AVANT d'y descendre (node_modules, .git, bin, obj…),
        // de consulter le jeton à chaque dossier et d'appliquer un plafond global.
        var oneLevel = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.System
        };

        await Task.Run(() =>
        {
            var visited = 0;

            foreach (var basePath in searchPaths)
            {
                if (!Directory.Exists(basePath)) continue;

                var pending = new Stack<(string Directory, int Depth)>();
                pending.Push((basePath, 0));

                while (pending.Count > 0)
                {
                    if (cancellationToken.IsCancellationRequested
                        || results.Count >= MaxResultsLimit
                        || visited >= MaxDirectoriesPerSearch)
                        return;

                    var (directory, depth) = pending.Pop();
                    visited++;

                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(directory, pattern, oneLevel))
                        {
                            if (cancellationToken.IsCancellationRequested || results.Count >= MaxResultsLimit)
                                return;

                            try
                            {
                                var info = new FileInfo(file);
                                results.Add(new SearchResult
                                {
                                    Name = Path.GetFileNameWithoutExtension(file),
                                    Path = file,
                                    Description = BuildDescription(file, info.Length, info.LastWriteTime),
                                    Type = DetermineResultType(file, false),
                                    Score = 400
                                });
                            }
                            catch { /* Ignorer les fichiers inaccessibles */ }
                        }

                        foreach (var subDirectory in Directory.EnumerateDirectories(directory, "*", oneLevel))
                        {
                            if (cancellationToken.IsCancellationRequested) return;

                            // Filtrer AVANT de descendre. Les jonctions héritées du profil
                            // (« Application Data »…) portent l'attribut Système et sont déjà
                            // écartées par AttributesToSkip ; les dossiers OneDrive (points
                            // d'analyse « cloud ») doivent en revanche rester parcourables.
                            if (IsExcludedDirectory(subDirectory))
                                continue;

                            var name = Path.GetFileName(subDirectory);
                            if (results.Count < MaxResultsLimit
                                && FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
                            {
                                results.Add(new SearchResult
                                {
                                    Name = name,
                                    Path = subDirectory,
                                    Description = subDirectory,
                                    Type = ResultType.Folder,
                                    Score = 400
                                });
                            }

                            if (depth + 1 <= MaxSearchDepth)
                                pending.Push((subDirectory, depth + 1));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DirectSearch] Error in {directory}: {ex.Message}");
                    }
                }
            }
        }, cancellationToken);

        return results.Take(MaxResultsLimit).ToList();
    }

    /// <summary>
    /// Dossier exclu de la recherche directe : comparaison par SEGMENT complet (« bin »,
    /// « obj »…) et par suite de segments pour les exclusions composées (« AppData\Local\Temp »).
    /// L'ancienne recherche de sous-chaîne excluait « C:\Users\Robin\… » (« bin »),
    /// « ObjectModel » ou « Objectifs 2026 » (« obj »).
    /// </summary>
    internal static bool IsExcludedDirectory(string directoryPath)
    {
        var segments = directoryPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var sequence in ExcludedFolderSegments)
        {
            for (var i = 0; i + sequence.Length <= segments.Length; i++)
            {
                var match = true;
                for (var j = 0; j < sequence.Length && match; j++)
                    match = segments[i + j].Equals(sequence[j], StringComparison.OrdinalIgnoreCase);
                if (match) return true;
            }
        }
        return false;
    }


    #endregion

    #region Helpers

    private static ResultType DetermineResultType(string path, bool isFolder)
    {
        if (isFolder) return ResultType.Folder;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".exe" or ".msi" => ResultType.Application,
            ".lnk" => ResultType.Application,
            ".bat" or ".cmd" or ".ps1" or ".vbs" => ResultType.Script,
            _ => ResultType.File
        };
    }

    private static string BuildDescription(string path, long? size, DateTime? modified)
    {
        var parts = new List<string>();

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            parts.Add(dir);
        if (size.HasValue)
            parts.Add(FormatFileSize(size.Value));

        if (modified.HasValue)
            parts.Add(modified.Value.ToString("dd/MM/yyyy HH:mm"));

        return string.Join(" • ", parts);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }

    #endregion
}

/// <summary>
/// Statut du moteur de recherche disponible.
/// </summary>
public enum SearchEngineStatus
{
    Everything,
    WindowsSearch,
    DirectSearch
}
/// <summary>
/// Informations détaillées sur le moteur de recherche.
/// </summary>
public class SearchEngineInfo
{
    public SearchEngineStatus Status { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool IsOptimal { get; init; }
    public required string Icon { get; init; }
    public string? Recommendation { get; init; }
}

/// <summary>
/// Résultat de recherche mis en cache.
/// </summary>
internal class CachedSearchResult
{
    public required string Query { get; init; }
    public required List<SearchResult> Results { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Wrapper P/Invoke pour Everything SDK.
/// </summary>
internal static class EverythingApi
{
    private const string DllName = "Everything64.dll";
    public const uint EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
    public const uint EVERYTHING_REQUEST_PATH = 0x00000002;
    public const uint EVERYTHING_REQUEST_SIZE = 0x00000010;
    public const uint EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000040;

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    private static extern uint Everything_SetSearchW(string lpSearchString);
    [DllImport(DllName)]
    private static extern void Everything_SetMax(uint dwMax);
    [DllImport(DllName)]
    private static extern void Everything_SetRequestFlags(uint dwRequestFlags);
    [DllImport(DllName)]
    private static extern bool Everything_QueryW(bool bWait);
    [DllImport(DllName)]
    private static extern uint Everything_GetNumResults();
    [DllImport(DllName, CharSet = CharSet.Unicode)]
    private static extern void Everything_GetResultFullPathNameW(uint nIndex, System.Text.StringBuilder lpString, uint nMaxCount);
    [DllImport(DllName)]
    private static extern bool Everything_IsFolderResult(uint nIndex);
    [DllImport(DllName)]
    private static extern bool Everything_GetResultSize(uint nIndex, out long lpFileSize);
    [DllImport(DllName)]
    private static extern bool Everything_GetResultDateModified(uint nIndex, out long lpFileTime);
    [DllImport(DllName)]
    private static extern uint Everything_GetLastError();
    [DllImport(DllName)]
    private static extern uint Everything_GetMajorVersion();
    [DllImport(DllName)]
    private static extern void Everything_Reset();
    [DllImport(DllName)]
    private static extern bool Everything_IsDBLoaded();
    private static bool? _isAvailable;

    public static bool IsAvailable()
    {
        if (_isAvailable.HasValue)
            return _isAvailable.Value;

        try
        {
            var version = Everything_GetMajorVersion();
            _isAvailable = version > 0 && Everything_IsDBLoaded();
        }
        catch
        {
            _isAvailable = false;
        }

        return _isAvailable.Value;
    }

    public static void RefreshAvailability() => _isAvailable = null;

    public static void Reset() => Everything_Reset();
    public static void SetSearch(string query) => Everything_SetSearchW(query);
    public static void SetMax(int max) => Everything_SetMax((uint)max);
    public static void SetRequestFlags(uint flags) => Everything_SetRequestFlags(flags);
    public static void Query(bool wait) => Everything_QueryW(wait);
    public static int GetNumResults() => (int)Everything_GetNumResults();

    public static string GetResultFullPathName(uint index)
    {
        var sb = new System.Text.StringBuilder(260);
        Everything_GetResultFullPathNameW(index, sb, 260);
        return sb.ToString();
    }

    public static bool IsFolder(uint index) => Everything_IsFolderResult(index);

    public static long? GetResultSize(uint index)
    {
        if (Everything_GetResultSize(index, out var size))
            return size;
        return null;
    }

    public static DateTime? GetResultDateModified(uint index)
    {
        if (Everything_GetResultDateModified(index, out var fileTime))
            return DateTime.FromFileTime(fileTime);
        return null;
    }
}