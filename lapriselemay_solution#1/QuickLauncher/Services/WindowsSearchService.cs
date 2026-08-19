using System.Data.OleDb;
using System.IO;
using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Service de recherche utilisant Windows Search API (indexeur Windows).
/// Permet une recherche rapide dans tous les fichiers indexés du système.
/// </summary>
public static class WindowsSearchService
{
    private const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows'";
    private const int MaxResults = 50;
    private const int TimeoutSeconds = 5;

    /// <summary>
    /// Recherche des fichiers via Windows Search API.
    /// </summary>
    /// <param name="query">Terme de recherche</param>
    /// <param name="searchScope">Dossier de recherche optionnel (null = tout le système)</param>
    /// <param name="cancellationToken">Token d'annulation</param>
    /// <returns>Liste des résultats de recherche</returns>
    public static async Task<List<SearchResult>> SearchAsync(
        string query, 
        string? searchScope = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        var results = new List<SearchResult>();

        try
        {
            // Échapper les caractères spéciaux SQL
            var escapedQuery = EscapeQuery(query);
            
            // Construire la requête SQL pour Windows Search
            var sql = BuildSearchQuery(escapedQuery, searchScope);

            await Task.Run(() =>
            {
                using var connection = new OleDbConnection(ConnectionString);
                connection.Open();

                using var command = new OleDbCommand(sql, connection);
                command.CommandTimeout = TimeoutSeconds;

                using var reader = command.ExecuteReader();
                while (reader.Read() && results.Count < MaxResults)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var result = CreateSearchResult(reader);
                    if (result != null)
                        results.Add(result);
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Recherche annulée
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowsSearch] Erreur: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Recherche rapide synchrone (pour les résultats immédiats).
    /// </summary>
    public static List<SearchResult> Search(string query, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        var results = new List<SearchResult>();

        try
        {
            var escapedQuery = EscapeQuery(query);
            var sql = BuildSearchQuery(escapedQuery, null, maxResults);

            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();

            using var command = new OleDbCommand(sql, connection);
            command.CommandTimeout = TimeoutSeconds;

            using var reader = command.ExecuteReader();
            while (reader.Read() && results.Count < maxResults)
            {
                var result = CreateSearchResult(reader);
                if (result != null)
                    results.Add(result);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowsSearch] Erreur: {ex.Message}");
        }

        return results;
    }

    private static string BuildSearchQuery(string query, string? scope, int maxResults = MaxResults)
    {
        var scopeClause = string.IsNullOrEmpty(scope) 
            ? "" 
            : $"AND SCOPE='file:{scope.Replace("'", "''")}' ";

        // Recherche par nom de fichier et contenu
        return $"""
            SELECT TOP {maxResults}
                System.ItemPathDisplay,
                System.ItemName,
                System.ItemType,
                System.Size,
                System.DateModified,
                System.Kind
            FROM SystemIndex
            WHERE (System.ItemName LIKE '%{query}%' OR CONTAINS(System.ItemName, '"{query}*"'))
            {scopeClause}
            ORDER BY System.Search.Rank DESC
            """;
    }

    private static SearchResult? CreateSearchResult(OleDbDataReader reader)
    {
        try
        {
            var path = reader["System.ItemPathDisplay"]?.ToString();
            var name = reader["System.ItemName"]?.ToString();
            var itemType = reader["System.ItemType"]?.ToString();
            var size = reader["System.Size"] as long?;
            var modified = reader["System.DateModified"] as DateTime?;
            var kind = reader["System.Kind"]?.ToString();

            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name))
                return null;

            // Déterminer le type de résultat
            var resultType = DetermineResultType(path, itemType, kind);

            // Construire la description
            var description = BuildDescription(path, size, modified);

            return new SearchResult
            {
                Name = name,
                Path = path,
                Description = description,
                Type = resultType,
                Score = 500 // Score moyen pour les résultats Windows Search
            };
        }
        catch
        {
            return null;
        }
    }

    private static ResultType DetermineResultType(string path, string? itemType, string? kind)
    {
        // Vérifier si c'est un dossier
        if (Directory.Exists(path))
            return ResultType.Folder;

        // Vérifier l'extension
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

        // Dossier parent
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            parts.Add(dir);

        // Taille
        if (size.HasValue)
            parts.Add(FormatFileSize(size.Value));

        // Date de modification
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

    private static string EscapeQuery(string query)
    {
        // Échapper les caractères spéciaux pour SQL et Windows Search.
        // '[' doit être échappé EN PREMIER : les remplacements de % et _
        // introduisent des crochets qui ne doivent pas être re-échappés
        // (sinon "a%b" → "a[%]b" → "a[[]%]b" et le % redevient un joker).
        return query
            .Replace("[", "[[]")
            .Replace("%", "[%]")
            .Replace("_", "[_]")
            .Replace("'", "''")
            .Replace("\"", "");
    }

    private static bool? _isAvailable;

    /// <summary>
    /// Vérifie si Windows Search est disponible (résultat mis en cache).
    /// Utiliser <see cref="RefreshAvailability"/> pour forcer une nouvelle vérification.
    /// </summary>
    public static bool IsAvailable()
    {
        if (_isAvailable.HasValue)
            return _isAvailable.Value;

        try
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();
            
            // Vérifier que le service répond réellement (pas juste la connexion)
            using var cmd = new OleDbCommand(
                "SELECT TOP 1 System.ItemName FROM SystemIndex", connection);
            cmd.CommandTimeout = 3;
            using var reader = cmd.ExecuteReader();
            _isAvailable = true;
        }
        catch
        {
            _isAvailable = false;
        }

        return _isAvailable.Value;
    }

    /// <summary>
    /// Force une nouvelle vérification de disponibilité.
    /// </summary>
    public static void RefreshAvailability()
    {
        _isAvailable = null;
    }
}
