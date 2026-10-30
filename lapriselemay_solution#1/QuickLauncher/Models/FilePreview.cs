using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickLauncher.Models;

/// <summary>
/// Données de prévisualisation d'un fichier.
/// </summary>
public sealed class FilePreview
{
    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public FilePreviewType PreviewType { get; init; }
    
    // Métadonnées
    public long FileSize { get; init; }
    public DateTime CreatedDate { get; init; }
    public DateTime ModifiedDate { get; init; }
    public DateTime AccessedDate { get; init; }
    public FileAttributes Attributes { get; init; }
    
    // Contenu (selon le type)
    public string? TextContent { get; init; }
    public ImageSource? ImagePreview { get; init; }
    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }
    
    // Nouvelles propriétés pour la prévisualisation de code
    public string? ProgrammingLanguage { get; init; }
    public int TotalLines { get; init; }
    public int PreviewLines { get; init; }
    public bool IsTruncated { get; init; }
    
    // Propriétés calculées
    public string FileSizeFormatted => FormatFileSize(FileSize);
    public string CreatedDateFormatted => CreatedDate.ToString("dd MMMM yyyy à HH:mm");
    public string ModifiedDateFormatted => ModifiedDate.ToString("dd MMMM yyyy à HH:mm");
    public string ImageDimensions => ImageWidth.HasValue && ImageHeight.HasValue 
        ? $"{ImageWidth}×{ImageHeight} px" 
        : string.Empty;
    
    public string LinesInfo => TotalLines > 0 
        ? (IsTruncated ? $"{PreviewLines}/{TotalLines} lignes" : $"{TotalLines} lignes")
        : string.Empty;
    
    public string LanguageDisplay => !string.IsNullOrEmpty(ProgrammingLanguage) 
        ? ProgrammingLanguage 
        : Extension;
    
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsReadOnly => (Attributes & FileAttributes.ReadOnly) != 0;
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;
    
    public string AttributesText
    {
        get
        {
            var attrs = new List<string>();
            if (IsHidden) attrs.Add("Caché");
            if (IsReadOnly) attrs.Add("Lecture seule");
            if (IsSystem) attrs.Add("Système");
            return attrs.Count > 0 ? string.Join(", ", attrs) : "Normal";
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["octets", "Ko", "Mo", "Go", "To"];
        var order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return order == 0 
            ? $"{size:0} {sizes[order]}" 
            : $"{size:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Type de prévisualisation disponible.
/// </summary>
public enum FilePreviewType
{
    None,
    Text,
    Image,
    Folder,
    Application,
    Audio,
    Video,
    Archive,
    Document
}

/// <summary>
/// Service de génération de prévisualisations.
/// </summary>
public static class FilePreviewService
{
    private const int MaxTextPreviewLines = 30;
    private const int MaxTextPreviewChars = 3000;
    private const int MaxImagePreviewSize = 400;
    
    private static readonly HashSet<string> TextExtensions = 
    [
        ".txt", ".md", ".json", ".xml", ".yaml", ".yml", ".csv", ".log",
        ".cs", ".js", ".ts", ".py", ".java", ".cpp", ".c", ".h", ".hpp",
        ".html", ".htm", ".css", ".scss", ".less", ".sql", ".sh", ".bat",
        ".cmd", ".ps1", ".ini", ".cfg", ".conf", ".config", ".gitignore",
        ".editorconfig", ".env", ".toml", ".rs", ".go", ".rb", ".php",
        ".vue", ".jsx", ".tsx", ".svelte", ".swift", ".kt", ".gradle",
        ".dockerfile", ".makefile", ".cmake", ".r", ".m", ".lua", ".pl"
    ];
    
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#",
        [".js"] = "JavaScript",
        [".ts"] = "TypeScript",
        [".jsx"] = "React JSX",
        [".tsx"] = "React TSX",
        [".py"] = "Python",
        [".java"] = "Java",
        [".cpp"] = "C++",
        [".c"] = "C",
        [".h"] = "C/C++ Header",
        [".hpp"] = "C++ Header",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".css"] = "CSS",
        [".scss"] = "SCSS",
        [".less"] = "LESS",
        [".json"] = "JSON",
        [".xml"] = "XML",
        [".yaml"] = "YAML",
        [".yml"] = "YAML",
        [".md"] = "Markdown",
        [".sql"] = "SQL",
        [".sh"] = "Shell",
        [".bat"] = "Batch",
        [".cmd"] = "Command",
        [".ps1"] = "PowerShell",
        [".rs"] = "Rust",
        [".go"] = "Go",
        [".rb"] = "Ruby",
        [".php"] = "PHP",
        [".swift"] = "Swift",
        [".kt"] = "Kotlin",
        [".vue"] = "Vue",
        [".svelte"] = "Svelte",
        [".lua"] = "Lua",
        [".r"] = "R",
        [".toml"] = "TOML",
        [".ini"] = "INI",
        [".cfg"] = "Config",
        [".conf"] = "Config",
        [".log"] = "Log",
        [".txt"] = "Text",
        [".csv"] = "CSV"
    };
    
    private static readonly HashSet<string> ImageExtensions = 
    [
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".tif"
    ];
    
    private static readonly HashSet<string> AudioExtensions = 
    [
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a"
    ];
    
    private static readonly HashSet<string> VideoExtensions = 
    [
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm"
    ];
    
    private static readonly HashSet<string> ArchiveExtensions = 
    [
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz"
    ];
    
    private static readonly HashSet<string> DocumentExtensions = 
    [
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp"
    ];

    /// <summary>
    /// Génère une prévisualisation pour un fichier ou dossier.
    /// </summary>
    /// <returns>L'aperçu, ou <c>null</c> (élément absent, erreur ou opération annulée).
    /// Ne lève jamais d'exception.</returns>
    public static async Task<FilePreview?> GeneratePreviewAsync(string path, CancellationToken token = default)
    {
        try
        {
            // Tout le travail (E/S comprises) hors du thread appelant, avec annulation effective
            return await Task.Run(() => GeneratePreviewCoreAsync(path, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<FilePreview?> GeneratePreviewCoreAsync(string path, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();

            if (Directory.Exists(path))
                return GenerateFolderPreview(path, token);

            if (File.Exists(path))
                return await GenerateFilePreviewAsync(path, token).ConfigureAwait(false);

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FilePreview] Erreur: {ex.Message}");
            return null;
        }
    }

    /// <summary>Nombre maximal d'entrées comptées pour l'aperçu d'un dossier.</summary>
    private const int MaxFolderEntriesCounted = 1000;

    private static FilePreview GenerateFolderPreview(string path, CancellationToken token)
    {
        var dirInfo = new DirectoryInfo(path);

        // Compter les éléments du dossier, avec un plafond (dossiers énormes, réseau)
        var fileCount = 0;
        var folderCount = 0;
        long totalSize = 0;
        var truncated = false;

        try
        {
            foreach (var entry in dirInfo.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (fileCount + folderCount >= MaxFolderEntriesCounted)
                {
                    truncated = true;
                    break;
                }

                if (entry is FileInfo file)
                {
                    fileCount++;
                    totalSize += file.Length;
                }
                else
                {
                    folderCount++;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Ignorer les erreurs d'accès */ }

        var content = truncated
            ? $"Plus de {MaxFolderEntriesCounted} éléments"
            : $"{fileCount} fichier(s), {folderCount} dossier(s)";

        return new FilePreview
        {
            FileName = dirInfo.Name,
            FullPath = path,
            Extension = "Dossier",
            PreviewType = FilePreviewType.Folder,
            FileSize = totalSize,
            CreatedDate = dirInfo.CreationTime,
            ModifiedDate = dirInfo.LastWriteTime,
            AccessedDate = dirInfo.LastAccessTime,
            Attributes = dirInfo.Attributes,
            TextContent = content
        };
    }

    /// <summary>
    /// Fichier « disponible en ligne uniquement » (OneDrive et autres fournisseurs cloud) :
    /// lire son contenu déclencherait son téléchargement juste pour un aperçu.
    /// </summary>
    private static bool IsCloudOnly(FileAttributes attributes)
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
        return (attributes & (FileAttributes.Offline | recallOnOpen | recallOnDataAccess)) != 0;
    }

    private static async Task<FilePreview> GenerateFilePreviewAsync(string path, CancellationToken token)
    {
        var fileInfo = new FileInfo(path);
        var cloudOnly = IsCloudOnly(fileInfo.Attributes);
        var ext = fileInfo.Extension.ToLowerInvariant();
        var previewType = DeterminePreviewType(ext);
        
        string? textContent = null;
        ImageSource? imagePreview = null;
        int? imageWidth = null;
        int? imageHeight = null;
        string? language = null;
        int totalLines = 0;
        int previewLines = 0;
        bool isTruncated = false;

        switch (previewType)
        {
            case FilePreviewType.Text when !cloudOnly:
                (textContent, totalLines, previewLines, isTruncated) = await ReadTextPreviewAsync(path, token).ConfigureAwait(false);
                LanguageNames.TryGetValue(ext, out language);
                break;

            case FilePreviewType.Image when !cloudOnly:
                token.ThrowIfCancellationRequested();
                (imagePreview, imageWidth, imageHeight) = await LoadImagePreviewAsync(path).ConfigureAwait(false);
                break;
        }

        return new FilePreview
        {
            FileName = fileInfo.Name,
            FullPath = path,
            Extension = string.IsNullOrEmpty(ext) ? "Fichier" : ext.ToUpperInvariant().TrimStart('.'),
            PreviewType = previewType,
            FileSize = fileInfo.Length,
            CreatedDate = fileInfo.CreationTime,
            ModifiedDate = fileInfo.LastWriteTime,
            AccessedDate = fileInfo.LastAccessTime,
            Attributes = fileInfo.Attributes,
            TextContent = textContent,
            ImagePreview = imagePreview,
            ImageWidth = imageWidth,
            ImageHeight = imageHeight,
            ProgrammingLanguage = language,
            TotalLines = totalLines,
            PreviewLines = previewLines,
            IsTruncated = isTruncated
        };
    }

    private static FilePreviewType DeterminePreviewType(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return FilePreviewType.None;
        
        if (TextExtensions.Contains(extension))
            return FilePreviewType.Text;
        
        if (ImageExtensions.Contains(extension))
            return FilePreviewType.Image;
        
        if (AudioExtensions.Contains(extension))
            return FilePreviewType.Audio;
        
        if (VideoExtensions.Contains(extension))
            return FilePreviewType.Video;
        
        if (ArchiveExtensions.Contains(extension))
            return FilePreviewType.Archive;
        
        if (DocumentExtensions.Contains(extension))
            return FilePreviewType.Document;
        
        if (extension is ".exe" or ".msi" or ".lnk")
            return FilePreviewType.Application;
        
        return FilePreviewType.None;
    }

    /// <summary>
    /// Lecture BORNÉE : au plus <see cref="MaxTextPreviewChars"/> caractères et
    /// <see cref="MaxTextPreviewLines"/> lignes, une ligne géante étant tronquée. Le fichier
    /// n'est plus relu en entier pour compter ses lignes : le total n'est connu (TotalLines)
    /// que si l'aperçu couvre tout le fichier.
    /// </summary>
    private static async Task<(string? Content, int TotalLines, int PreviewLines, bool IsTruncated)> ReadTextPreviewAsync(string path, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            var buffer = new char[MaxTextPreviewChars + 1];
            var read = 0;
            var endOfFile = false;
            while (read < buffer.Length)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(read), token).ConfigureAwait(false);
                if (count == 0)
                {
                    endOfFile = true;
                    break;
                }
                read += count;
            }

            var text = new string(buffer, 0, Math.Min(read, MaxTextPreviewChars));
            var lines = text.Split('\n');
            if (endOfFile && lines.Length > 1 && lines[^1].Length == 0)
                lines = lines[..^1]; // saut de ligne final

            var shown = new List<string>();
            foreach (var line in lines.Take(MaxTextPreviewLines))
                shown.Add($"{shown.Count + 1,4} │ {line.TrimEnd('\r')}");

            var isTruncated = !endOfFile || read > MaxTextPreviewChars || lines.Length > MaxTextPreviewLines;
            var content = string.Join(Environment.NewLine, shown);
            if (isTruncated)
                content += Environment.NewLine + "      ⋮ (aperçu tronqué)";

            return (content, isTruncated ? 0 : shown.Count, shown.Count, isTruncated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ("[Impossible de lire le fichier]", 0, 0, false);
        }
    }

    private static async Task<(ImageSource?, int?, int?)> LoadImagePreviewAsync(string path)
    {
        try
        {
            return await Task.Run(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = MaxImagePreviewSize;
                bitmap.EndInit();
                bitmap.Freeze();
                
                // Récupérer les dimensions originales
                var decoder = BitmapDecoder.Create(
                    new Uri(path),
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);
                
                var frame = decoder.Frames[0];
                var width = frame.PixelWidth;
                var height = frame.PixelHeight;
                
                return (bitmap as ImageSource, (int?)width, (int?)height);
            });
        }
        catch
        {
            return (null, null, null);
        }
    }
}
