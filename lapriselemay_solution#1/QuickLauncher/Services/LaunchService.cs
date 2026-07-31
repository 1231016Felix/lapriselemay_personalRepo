using System;
using System.Diagnostics;
using System.IO;
using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Abstraction pour le lancement de fichiers, applications et commandes système.
/// Permet l'injection de dépendances et la testabilité (Point #6).
///
/// <b>Contrat :</b> les méthodes retournent <c>false</c> quand le lancement a échoué,
/// afin que l'appelant puisse en informer l'utilisateur. Un échec est toujours journalisé.
/// </summary>
public interface ILaunchService
{
    /// <summary>Lance un item. Retourne false si le lancement a échoué.</summary>
    bool Launch(SearchResult item);

    void OpenContainingFolder(SearchResult item);

    /// <summary>Lance un item avec élévation UAC. Retourne false si le lancement a échoué.</summary>
    bool RunAsAdmin(SearchResult item);
}

public class LaunchService : ILaunchService
{
    private readonly IStoreAppService _storeAppService;
    private readonly IShortcutHelper _shortcutHelper;
    private readonly ILogger _logger;

    // Injection des services par le constructeur
    public LaunchService(IStoreAppService storeAppService, IShortcutHelper shortcutHelper, ILogger logger)
    {
        _storeAppService = storeAppService ?? throw new ArgumentNullException(nameof(storeAppService));
        _shortcutHelper = shortcutHelper ?? throw new ArgumentNullException(nameof(shortcutHelper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool Launch(SearchResult item)
    {
        try
        {
            switch (item.Type)
            {
                case ResultType.Application:
                case ResultType.File:
                    // Les apps issues de shell:AppsFolder sans chemin fichier
                    // (ex: AppUserModelId comme "Microsoft.VisualStudio.Installer")
                    // doivent être lancées via shell:AppsFolder
                    if (item.Type == ResultType.Application && !IsFileSystemPath(item.Path))
                    {
                        if (!_storeAppService.LaunchApp(item.Path))
                            LaunchApplication(item.Path);
                    }
                    else
                    {
                        LaunchApplication(item.Path);
                    }
                    return true;

                case ResultType.StoreApp:
                    // Utiliser shell:AppsFolder pour toutes les apps de AppsFolder
                    if (!_storeAppService.LaunchApp(item.Path))
                    {
                        // Fallback: essayer de lancer directement
                        LaunchApplication(item.Path);
                    }
                    return true;

                case ResultType.Folder:
                    StartProcess("explorer.exe", $"\"{item.Path}\"");
                    return true;

                case ResultType.Script:
                    LaunchScript(item);
                    return true;

                case ResultType.WebSearch:
                case ResultType.Bookmark:
                    StartProcess(item.Path);
                    return true;

                case ResultType.SystemControl:
                case ResultType.AppControl:
                case ResultType.SystemCommand:
                    LaunchSystemControl(item.Path);
                    return true;

                case ResultType.Calculator:
                    System.Windows.Clipboard.SetText(item.Path);
                    return true;

                case ResultType.Command:
                    StartProcess("cmd.exe", $"/c {item.Path}");
                    return true;

                default:
                    // Type sans action de lancement (Note, SearchHistory déjà réaiguillé, etc.)
                    _logger.Warning($"Lancement non supporté pour le type {item.Type} ('{item.Name}')");
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Échec du lancement de '{item.Name}' [{item.Type}] → {item.Path}", ex);
            return false;
        }
    }

    private void LaunchApplication(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Pour les fichiers .lnk, résoudre le raccourci si nécessaire
        if (ext == ".lnk")
        {
            var info = _shortcutHelper.ResolveShortcut(path);
            if (info != null && !string.IsNullOrEmpty(info.TargetPath))
            {
                // Vérifier si la cible existe
                if (File.Exists(info.TargetPath) || Directory.Exists(info.TargetPath))
                {
                    var workingDir = !string.IsNullOrEmpty(info.WorkingDirectory)
                        ? info.WorkingDirectory
                        : Path.GetDirectoryName(info.TargetPath);

                    if (!string.IsNullOrEmpty(info.Arguments))
                        StartProcess(info.TargetPath, info.Arguments, workingDir);
                    else
                        StartProcess(info.TargetPath, workingDirectory: workingDir);
                    return;
                }

                // La cible n'existe pas mais c'est peut-être une URL ou un protocole
                if (info.TargetPath.Contains("://") || info.TargetPath.StartsWith("steam:"))
                {
                    StartProcess(info.TargetPath);
                    return;
                }
            }
        }

        // Lancement direct (fonctionne pour .exe, .lnk avec UseShellExecute, URLs, etc.)
        StartProcess(path);
    }

    private void LaunchScript(SearchResult item)
    {
        var ext = Path.GetExtension(item.Path).ToLowerInvariant();
        var workingDir = Path.GetDirectoryName(item.Path) ?? "";

        if (ext == ".ps1")
            StartProcess("powershell.exe", $"-ExecutionPolicy Bypass -File \"{item.Path}\"", workingDir);
        else
            StartProcess(item.Path, workingDirectory: workingDir);
    }

    public void OpenContainingFolder(SearchResult item)
    {
        var folder = Path.GetDirectoryName(item.Path);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            StartProcess("explorer.exe", $"/select,\"{item.Path}\"");
    }

    public bool RunAsAdmin(SearchResult item)
    {
        try
        {
            var path = item.Path;
            var arguments = string.Empty;
            var workingDirectory = string.Empty;

            // 1. Gérer les applications du Windows Store (ex: PowerShell 7, Windows Terminal)
            // Elles n'ont pas de chemin fichier classique (AppUserModelId)
            if (!IsFileSystemPath(path) && (item.Type == ResultType.Application || item.Type == ResultType.StoreApp))
            {
                // L'ajout de "shell:AppsFolder\" permet à Windows d'accepter le verbe "runas" sur une App UWP
                path = $"shell:AppsFolder\\{path}";
            }
            // 2. Gérer les raccourcis classiques (.lnk)
            else if (Path.GetExtension(path).ToLowerInvariant() == ".lnk")
            {
                var info = _shortcutHelper.ResolveShortcut(path);
                if (info != null && !string.IsNullOrEmpty(info.TargetPath))
                {
                    if (File.Exists(info.TargetPath) || Directory.Exists(info.TargetPath))
                    {
                        path = info.TargetPath;
                        arguments = info.Arguments ?? string.Empty;
                        workingDirectory = !string.IsNullOrEmpty(info.WorkingDirectory)
                            ? info.WorkingDirectory
                            : Path.GetDirectoryName(info.TargetPath) ?? string.Empty;
                    }
                }
            }

            // 3. Configuration du processus d'élévation
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "runas" // C'est ici que l'élévation UAC (Admin) est demandée
            };

            if (!string.IsNullOrEmpty(arguments))
                psi.Arguments = arguments;

            if (!string.IsNullOrEmpty(workingDirectory))
                psi.WorkingDirectory = workingDirectory;

            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Code 1223 = L'utilisateur a annulé l'élévation UAC (fenêtre "Voulez-vous autoriser...").
            // Ce n'est pas un échec applicatif : ne pas alerter l'utilisateur sur sa propre décision.
            _logger.Info($"Élévation UAC annulée par l'utilisateur pour '{item.Name}'");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Échec du lancement en administrateur de '{item.Name}' → {item.Path}", ex);
            return false;
        }
    }

    /// <summary>
    /// Lance un item de paramètres Windows.
    /// Gère les URIs ms-settings:, les commandes control| et les .msc.
    /// </summary>
    private void LaunchSystemControl(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(":weather:") || path.StartsWith(":timer:"))
            return;

        // Format "control|args" pour les applets du panneau de configuration
        if (path.StartsWith("control|"))
        {
            var args = path["control|".Length..];
            StartProcess("control.exe", string.IsNullOrEmpty(args) ? null : args);
            return;
        }

        // ms-settings: URIs, .msc, mstsc, etc. → lancement direct
        StartProcess(path);
    }

    /// <summary>
    /// Vérifie si un chemin ressemble à un chemin fichier Windows (ex: C:\...)
    /// </summary>
    private static bool IsFileSystemPath(string path)
        => path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    /// <summary>
    /// Démarre un processus via le shell. Lève une exception en cas d'échec réel
    /// (fichier introuvable, accès refusé), ce que <see cref="Launch"/> intercepte.
    ///
    /// Note : avec <c>UseShellExecute = true</c>, <see cref="Process.Start(ProcessStartInfo)"/>
    /// retourne <c>null</c> quand le shell confie l'ouverture à une instance déjà lancée
    /// (cas courant pour les documents). Ce n'est PAS un échec — ne pas tester la valeur de retour.
    /// </summary>
    private static void StartProcess(string fileName, string? arguments = null, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = true
        };

        if (!string.IsNullOrEmpty(arguments))
            psi.Arguments = arguments;

        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        Process.Start(psi);
    }
}