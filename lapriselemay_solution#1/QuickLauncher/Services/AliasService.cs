using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Service de gestion des alias personnalisés pour les applications.
/// Permet de définir des raccourcis textuels pour lancer des programmes.
/// Ex: "code" -> Visual Studio Code, "ff" -> Firefox
/// </summary>
public sealed class AliasService : IDisposable
{
    private readonly string _aliasFilePath;
    private readonly Dictionary<string, AliasEntry> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? _watcher;
    private readonly object _lock = new();
    private bool _disposed;

    public event EventHandler? AliasesChanged;

    public int Count => _aliases.Count;

    public AliasService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Constants.AppName);
        
        Directory.CreateDirectory(appData);
        _aliasFilePath = Path.Combine(appData, "aliases.json");

        Load();

        // Surveiller les changements du fichier
        try
        {
            _watcher = new FileSystemWatcher(appData, "aliases.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };
            _watcher.Changed += OnFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* Ignorer si la surveillance échoue */ }
    }

    #region CRUD Operations

    /// <summary>
    /// Ajoute ou met à jour un alias.
    /// </summary>
    /// <param name="targetType">Type du résultat d'origine (raccourci, app Store, dossier…) :
    /// l'alias sera lancé exactement comme cet élément. Null = cible textuelle (commande).</param>
    /// <returns><c>true</c> si l'alias a été écrit sur disque.</returns>
    public bool SetAlias(string alias, string targetPath, string? description = null, ResultType? targetType = null)
    {
        if (string.IsNullOrWhiteSpace(alias)) return false;

        bool saved;
        lock (_lock)
        {
            _aliases[alias.ToLowerInvariant()] = new AliasEntry
            {
                Alias = alias.ToLowerInvariant(),
                TargetPath = targetPath,
                TargetType = targetType,
                Description = description,
                CreatedAt = DateTime.Now,
                UseCount = _aliases.TryGetValue(alias.ToLowerInvariant(), out var existing)
                    ? existing.UseCount
                    : 0
            };
            saved = Save();
        }

        AliasesChanged?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    /// <summary>
    /// Supprime un alias
    /// </summary>
    public bool RemoveAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return false;

        bool removed;
        lock (_lock)
        {
            removed = _aliases.Remove(alias.ToLowerInvariant());
            if (removed) Save();
        }

        if (removed)
            AliasesChanged?.Invoke(this, EventArgs.Empty);

        return removed;
    }

    /// <summary>
    /// Récupère un alias par son nom
    /// </summary>
    public AliasEntry? GetAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;

        lock (_lock)
        {
            return _aliases.TryGetValue(alias.ToLowerInvariant(), out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// Vérifie si un alias existe
    /// </summary>
    public bool HasAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return false;

        lock (_lock)
        {
            return _aliases.ContainsKey(alias.ToLowerInvariant());
        }
    }

    /// <summary>
    /// Retourne le premier alias associé à un chemin cible, ou null.
    /// </summary>
    public AliasEntry? GetAliasByTargetPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return null;

        lock (_lock)
        {
            return _aliases.Values.FirstOrDefault(a => 
                string.Equals(a.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Retourne tous les alias
    /// </summary>
    public List<AliasEntry> GetAllAliases()
    {
        lock (_lock)
        {
            return _aliases.Values.OrderBy(a => a.Alias).ToList();
        }
    }

    /// <summary>
    /// Recherche les alias correspondant à une requête
    /// </summary>
    public List<SearchResult> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var results = new List<SearchResult>();
        var queryLower = query.ToLowerInvariant();

        lock (_lock)
        {
            foreach (var entry in _aliases.Values)
            {
                // Correspondance exacte de l'alias
                if (entry.Alias == queryLower)
                {
                    results.Add(CreateSearchResult(entry, 1000));
                }
                // L'alias commence par la requête
                else if (entry.Alias.StartsWith(queryLower))
                {
                    results.Add(CreateSearchResult(entry, 800));
                }
                // L'alias contient la requête
                else if (entry.Alias.Contains(queryLower))
                {
                    results.Add(CreateSearchResult(entry, 500));
                }
            }
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>
    /// Incrémente le compteur d'utilisation d'un alias
    /// </summary>
    public void RecordUsage(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return;

        lock (_lock)
        {
            if (_aliases.TryGetValue(alias.ToLowerInvariant(), out var entry))
            {
                entry.UseCount++;
                entry.LastUsed = DateTime.Now;
                Save();
            }
        }
    }

    #endregion

    #region Import/Export

    /// <summary>
    /// Importe des alias depuis un fichier JSON
    /// </summary>
    public int ImportFromJson(string json)
    {
        try
        {
            var imported = JsonSerializer.Deserialize<List<AliasEntry>>(json);
            if (imported == null) return 0;

            int count = 0;
            lock (_lock)
            {
                foreach (var entry in imported)
                {
                    if (!string.IsNullOrEmpty(entry.Alias) && !string.IsNullOrEmpty(entry.TargetPath))
                    {
                        _aliases[entry.Alias.ToLowerInvariant()] = entry;
                        count++;
                    }
                }
                Save();
            }

            if (count > 0)
                AliasesChanged?.Invoke(this, EventArgs.Empty);

            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Exporte tous les alias en JSON
    /// </summary>
    public string ExportToJson()
    {
        lock (_lock)
        {
            return JsonSerializer.Serialize(_aliases.Values.ToList(), new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
        }
    }

    #endregion

    #region Default Aliases

    /// <summary>
    /// Ajoute les alias par défaut communs
    /// </summary>
    public void AddDefaultAliases()
    {
        var defaults = new Dictionary<string, (string Path, string Description)>
        {
            // Navigateurs
            ["ff"] = ("firefox", "Firefox"),
            ["chrome"] = ("chrome", "Google Chrome"),
            ["edge"] = ("msedge", "Microsoft Edge"),
            
            // Développement
            ["code"] = ("code", "Visual Studio Code"),
            ["vs"] = ("devenv", "Visual Studio"),
            ["term"] = ("wt", "Windows Terminal"),
            ["cmd"] = ("cmd", "Invite de commandes"),
            ["ps"] = ("powershell", "PowerShell"),
            
            // Utilitaires
            ["calc"] = ("calc", "Calculatrice"),
            ["note"] = ("notepad", "Bloc-notes"),
            ["paint"] = ("mspaint", "Paint"),
            ["explorer"] = ("explorer", "Explorateur de fichiers"),
            
            // Système
            ["settings"] = ("ms-settings:", "Paramètres Windows"),
            ["control"] = ("control", "Panneau de configuration"),
            ["task"] = ("taskmgr", "Gestionnaire des tâches"),
        };

        lock (_lock)
        {
            foreach (var (alias, (path, description)) in defaults)
            {
                if (!_aliases.ContainsKey(alias))
                {
                    _aliases[alias] = new AliasEntry
                    {
                        Alias = alias,
                        TargetPath = path,
                        Description = description,
                        CreatedAt = DateTime.Now,
                        IsDefault = true
                    };
                }
            }
            Save();
        }

        AliasesChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Persistence

    private void Load()
    {
        var entries = TryRead(_aliasFilePath) ?? TryRead(BackupPath);
        if (entries == null) return;

        try
        {
            lock (_lock)
            {
                _aliases.Clear();
                foreach (var entry in entries)
                {
                    if (!string.IsNullOrEmpty(entry.Alias))
                    {
                        _aliases[entry.Alias.ToLowerInvariant()] = entry;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Alias] Échec du chargement des alias", ex);
        }
    }

    private string BackupPath => _aliasFilePath + ".bak";

    /// <summary>Lit une liste d'alias ; null si le fichier est absent ou illisible.</summary>
    private static List<AliasEntry>? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<AliasEntry>>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Warning($"[Alias] Fichier d'alias illisible '{Path.GetFileName(path)}' : {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Écrit les alias de façon atomique (fichier temporaire puis remplacement) en gardant la
    /// version précédente dans « aliases.json.bak ». Un échec est journalisé et signalé.
    /// </summary>
    private bool Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_aliases.Values.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var tempPath = _aliasFilePath + ".tmp";
            File.WriteAllText(tempPath, json);

            if (File.Exists(_aliasFilePath))
                File.Replace(tempPath, _aliasFilePath, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, _aliasFilePath);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("[Alias] Échec de la sauvegarde des alias", ex);
            return false;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Recharger après un délai pour éviter les conflits
        Task.Delay(100).ContinueWith(_ =>
        {
            Load();
            AliasesChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    #endregion

    #region Helpers

    private static SearchResult CreateSearchResult(AliasEntry entry, int baseScore)
    {
        return new SearchResult
        {
            Name = entry.Alias,
            Path = entry.TargetPath,
            Description = entry.Description ?? $"Alias → {entry.TargetPath}",
            // Type d'origine conservé : l'alias est lancé exactement comme l'élément qu'il
            // désigne (raccourci, app Store, dossier, favori…). Sans type connu (alias par
            // défaut, saisi à la main ou ancien), la cible est une commande, interprétée
            // sans jamais passer brute par cmd.exe (cf. AliasCommand).
            Type = ToLaunchType(entry.TargetType),
            Score = baseScore + Math.Min(entry.UseCount * 5, 100),
            UseCount = entry.UseCount,
            LastUsed = entry.LastUsed
        };
    }

    private static ResultType ToLaunchType(ResultType? targetType) => targetType switch
    {
        ResultType.Application or ResultType.StoreApp or ResultType.File or ResultType.Folder
            or ResultType.Script or ResultType.Bookmark or ResultType.WebSearch
            or ResultType.SystemControl or ResultType.AppControl => targetType.Value,
        _ => ResultType.Command
    };

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watcher?.Dispose();
    }
}

/// <summary>
/// Entrée d'alias avec métadonnées
/// </summary>
public class AliasEntry
{
    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Type du résultat d'origine. Absent pour les alias par défaut, saisis à la main ou
    /// antérieurs à ce champ : la cible est alors interprétée comme une commande.
    /// </summary>
    [JsonPropertyName("targetType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResultType? TargetType { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("lastUsed")]
    public DateTime LastUsed { get; set; }

    [JsonPropertyName("useCount")]
    public int UseCount { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }
}
