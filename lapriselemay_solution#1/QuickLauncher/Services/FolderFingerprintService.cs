using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuickLauncher.Services;

/// <summary>
/// Service de fingerprinting des dossiers pour l'indexation différentielle.
/// Permet de détecter les changements sans rescanner entièrement chaque dossier.
/// </summary>
public sealed class FolderFingerprintService : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, FolderFingerprint> _fingerprints = new();
    private bool _disposed;

    /// <summary>
    /// Représente l'empreinte d'un dossier pour détecter les changements.
    /// </summary>
    public sealed class FolderFingerprint
    {
        public string Path { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public int FolderCount { get; set; }
        public long TotalSize { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime ComputedAt { get; set; }
    }

    /// <summary>
    /// Résultat de la comparaison des fingerprints.
    /// </summary>
    public sealed class FingerprintComparison
    {
        public List<string> NewFolders { get; } = [];
        public List<string> ModifiedFolders { get; } = [];
        public List<string> DeletedFolders { get; } = [];
        public List<string> UnchangedFolders { get; } = [];
        
        public bool HasChanges => NewFolders.Count > 0 || ModifiedFolders.Count > 0 || DeletedFolders.Count > 0;
        public int TotalChanges => NewFolders.Count + ModifiedFolders.Count + DeletedFolders.Count;
    }

    public FolderFingerprintService(string? databasePath = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Constants.AppName);
        
        Directory.CreateDirectory(appData);
        _dbPath = databasePath ?? Path.Combine(appData, "fingerprints.db");
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";
        
        InitializeDatabase();
        LoadFingerprints();
    }

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Fingerprints (
                Path TEXT PRIMARY KEY,
                Hash TEXT NOT NULL,
                FileCount INTEGER NOT NULL,
                FolderCount INTEGER NOT NULL,
                TotalSize INTEGER NOT NULL,
                LastModified TEXT NOT NULL,
                ComputedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_fingerprint_hash ON Fingerprints(Hash);
            """;
        cmd.ExecuteNonQuery();
    }

    private void LoadFingerprints()
    {
        _fingerprints.Clear();
        
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Path, Hash, FileCount, FolderCount, TotalSize, LastModified, ComputedAt FROM Fingerprints";
        
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var fp = new FolderFingerprint
            {
                Path = reader.GetString(0),
                Hash = reader.GetString(1),
                FileCount = reader.GetInt32(2),
                FolderCount = reader.GetInt32(3),
                TotalSize = reader.GetInt64(4),
                LastModified = DateTime.Parse(reader.GetString(5)),
                ComputedAt = DateTime.Parse(reader.GetString(6))
            };
            _fingerprints[fp.Path] = fp;
        }
        
        Debug.WriteLine($"[FolderFingerprint] Loaded {_fingerprints.Count} fingerprints from database");
    }

    /// <summary>
    /// Calcule le fingerprint d'un dossier de manière rapide.
    /// Utilise les métadonnées (dates, tailles) plutôt que le contenu des fichiers.
    /// </summary>
    public FolderFingerprint ComputeFingerprint(string folderPath, IEnumerable<string> extensions, int maxDepth, bool includeHidden)
    {
        var sw = Stopwatch.StartNew();
        
        var fileCount = 0;
        var folderCount = 0;
        long totalSize = 0;
        var latestModified = DateTime.MinValue;
        
        // StringBuilder pour construire le hash
        var hashInput = new StringBuilder();
        hashInput.AppendLine(folderPath);
        
        var extensionSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        
        try
        {
            ComputeFolderStats(
                folderPath, 
                extensionSet, 
                maxDepth, 
                includeHidden, 
                0,
                ref fileCount, 
                ref folderCount, 
                ref totalSize, 
                ref latestModified,
                hashInput);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FolderFingerprint] Error computing stats for {folderPath}: {ex.Message}");
        }
        
        // Ajouter les stats au hash input
        hashInput.AppendLine($"{fileCount}|{folderCount}|{totalSize}|{latestModified:O}");
        
        // Calculer le hash final
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(hashInput.ToString()));
        var hash = Convert.ToHexString(hashBytes);
        
        sw.Stop();
        Debug.WriteLine($"[FolderFingerprint] Computed fingerprint for {Path.GetFileName(folderPath)} in {sw.ElapsedMilliseconds}ms: {fileCount} files, {folderCount} folders");
        
        return new FolderFingerprint
        {
            Path = folderPath,
            Hash = hash,
            FileCount = fileCount,
            FolderCount = folderCount,
            TotalSize = totalSize,
            LastModified = latestModified,
            ComputedAt = DateTime.UtcNow
        };
    }

    private void ComputeFolderStats(
        string folderPath,
        HashSet<string> extensions,
        int maxDepth,
        bool includeHidden,
        int currentDepth,
        ref int fileCount,
        ref int folderCount,
        ref long totalSize,
        ref DateTime latestModified,
        StringBuilder hashInput)
    {
        if (currentDepth > maxDepth) return;
        
        try
        {
            var dirInfo = new DirectoryInfo(folderPath);
            
            if (!includeHidden && (dirInfo.Attributes & FileAttributes.Hidden) != 0)
                return;
            
            folderCount++;
            
            // Ajouter les infos du dossier au hash
            hashInput.AppendLine($"D:{dirInfo.Name}:{dirInfo.LastWriteTimeUtc:O}");
            
            // Traiter les fichiers
            foreach (var file in dirInfo.EnumerateFiles())
            {
                try
                {
                    if (!includeHidden && (file.Attributes & FileAttributes.Hidden) != 0)
                        continue;
                    
                    var ext = file.Extension.ToLowerInvariant();
                    if (!extensions.Contains(ext))
                        continue;
                    
                    fileCount++;
                    totalSize += file.Length;
                    
                    if (file.LastWriteTimeUtc > latestModified)
                        latestModified = file.LastWriteTimeUtc;
                    
                    // Ajouter au hash (nom + taille + date)
                    hashInput.AppendLine($"F:{file.Name}:{file.Length}:{file.LastWriteTimeUtc:O}");
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            
            // Récursion sur les sous-dossiers
            foreach (var subDir in dirInfo.EnumerateDirectories())
            {
                try
                {
                    ComputeFolderStats(
                        subDir.FullName,
                        extensions,
                        maxDepth,
                        includeHidden,
                        currentDepth + 1,
                        ref fileCount,
                        ref folderCount,
                        ref totalSize,
                        ref latestModified,
                        hashInput);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// Compare les fingerprints actuels avec ceux stockés pour déterminer les changements.
    /// </summary>
    public FingerprintComparison CompareWithStored(IEnumerable<string> currentFolders, IEnumerable<string> extensions, int maxDepth, bool includeHidden)
    {
        var comparison = new FingerprintComparison();
        var currentFolderSet = new HashSet<string>(currentFolders, StringComparer.OrdinalIgnoreCase);
        var extensionList = extensions.ToList();
        
        // Vérifier les dossiers actuels
        foreach (var folder in currentFolderSet)
        {
            if (!Directory.Exists(folder))
            {
                comparison.DeletedFolders.Add(folder);
                continue;
            }
            
            // Calculer le nouveau fingerprint
            var newFp = ComputeFingerprint(folder, extensionList, maxDepth, includeHidden);
            
            if (_fingerprints.TryGetValue(folder, out var storedFp))
            {
                // Comparer les hashes
                if (storedFp.Hash == newFp.Hash)
                {
                    comparison.UnchangedFolders.Add(folder);
                }
                else
                {
                    comparison.ModifiedFolders.Add(folder);
                    Debug.WriteLine($"[FolderFingerprint] Modified: {Path.GetFileName(folder)} (files: {storedFp.FileCount} -> {newFp.FileCount})");
                }
            }
            else
            {
                comparison.NewFolders.Add(folder);
                Debug.WriteLine($"[FolderFingerprint] New folder: {Path.GetFileName(folder)}");
            }
        }
        
        // Vérifier les dossiers supprimés (dans le store mais plus dans la config)
        foreach (var storedPath in _fingerprints.Keys)
        {
            if (!currentFolderSet.Contains(storedPath))
            {
                comparison.DeletedFolders.Add(storedPath);
                Debug.WriteLine($"[FolderFingerprint] Deleted: {Path.GetFileName(storedPath)}");
            }
        }
        
        Debug.WriteLine($"[FolderFingerprint] Comparison: {comparison.NewFolders.Count} new, {comparison.ModifiedFolders.Count} modified, {comparison.DeletedFolders.Count} deleted, {comparison.UnchangedFolders.Count} unchanged");
        
        return comparison;
    }

    /// <summary>
    /// Sauvegarde un fingerprint dans la base de données.
    /// </summary>
    public void SaveFingerprint(FolderFingerprint fingerprint)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO Fingerprints 
            (Path, Hash, FileCount, FolderCount, TotalSize, LastModified, ComputedAt)
            VALUES (@path, @hash, @fileCount, @folderCount, @totalSize, @lastModified, @computedAt)
            """;
        
        cmd.Parameters.AddWithValue("@path", fingerprint.Path);
        cmd.Parameters.AddWithValue("@hash", fingerprint.Hash);
        cmd.Parameters.AddWithValue("@fileCount", fingerprint.FileCount);
        cmd.Parameters.AddWithValue("@folderCount", fingerprint.FolderCount);
        cmd.Parameters.AddWithValue("@totalSize", fingerprint.TotalSize);
        cmd.Parameters.AddWithValue("@lastModified", fingerprint.LastModified.ToString("O"));
        cmd.Parameters.AddWithValue("@computedAt", fingerprint.ComputedAt.ToString("O"));
        
        cmd.ExecuteNonQuery();
        
        _fingerprints[fingerprint.Path] = fingerprint;
    }

    /// <summary>
    /// Sauvegarde plusieurs fingerprints de manière optimisée.
    /// </summary>
    public void SaveFingerprints(IEnumerable<FolderFingerprint> fingerprints)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        
        using var transaction = conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO Fingerprints 
                (Path, Hash, FileCount, FolderCount, TotalSize, LastModified, ComputedAt)
                VALUES (@path, @hash, @fileCount, @folderCount, @totalSize, @lastModified, @computedAt)
                """;
            
            var pathParam = cmd.Parameters.Add("@path", SqliteType.Text);
            var hashParam = cmd.Parameters.Add("@hash", SqliteType.Text);
            var fileCountParam = cmd.Parameters.Add("@fileCount", SqliteType.Integer);
            var folderCountParam = cmd.Parameters.Add("@folderCount", SqliteType.Integer);
            var totalSizeParam = cmd.Parameters.Add("@totalSize", SqliteType.Integer);
            var lastModifiedParam = cmd.Parameters.Add("@lastModified", SqliteType.Text);
            var computedAtParam = cmd.Parameters.Add("@computedAt", SqliteType.Text);
            
            foreach (var fp in fingerprints)
            {
                pathParam.Value = fp.Path;
                hashParam.Value = fp.Hash;
                fileCountParam.Value = fp.FileCount;
                folderCountParam.Value = fp.FolderCount;
                totalSizeParam.Value = fp.TotalSize;
                lastModifiedParam.Value = fp.LastModified.ToString("O");
                computedAtParam.Value = fp.ComputedAt.ToString("O");
                
                cmd.ExecuteNonQuery();
                _fingerprints[fp.Path] = fp;
            }
            
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Supprime un fingerprint de la base de données.
    /// </summary>
    public void RemoveFingerprint(string folderPath)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Fingerprints WHERE Path = @path";
        cmd.Parameters.AddWithValue("@path", folderPath);
        cmd.ExecuteNonQuery();
        
        _fingerprints.TryRemove(folderPath, out _);
    }

    /// <summary>
    /// Obtient le fingerprint stocké pour un dossier.
    /// </summary>
    public FolderFingerprint? GetStoredFingerprint(string folderPath)
    {
        return _fingerprints.TryGetValue(folderPath, out var fp) ? fp : null;
    }

    /// <summary>
    /// Obtient des statistiques sur les fingerprints stockés.
    /// </summary>
    public (int Count, long TotalFiles, long TotalSize) GetStats()
    {
        var totalFiles = _fingerprints.Values.Sum(f => f.FileCount);
        var totalSize = _fingerprints.Values.Sum(f => f.TotalSize);
        return (_fingerprints.Count, totalFiles, totalSize);
    }

    /// <summary>
    /// Efface tous les fingerprints stockés.
    /// </summary>
    public void ClearAll()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Fingerprints";
        cmd.ExecuteNonQuery();
        
        _fingerprints.Clear();
        Debug.WriteLine("[FolderFingerprint] All fingerprints cleared");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fingerprints.Clear();
        GC.SuppressFinalize(this);
    }
}
