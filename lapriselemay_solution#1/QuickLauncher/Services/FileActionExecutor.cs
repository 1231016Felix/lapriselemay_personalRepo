using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using QuickLauncher.Models;

using Clipboard = System.Windows.Clipboard;

namespace QuickLauncher.Services;

/// <summary>
/// Abstraction pour l'exécution des actions sur les fichiers.
/// Extrait de FileAction.cs (Amélioration #1 : séparation des responsabilités).
/// </summary>
public interface IFileActionExecutor
{
    /// <summary>
    /// Exécute une action sur un chemin.
    /// </summary>
    bool Execute(FileActionType actionType, string path);
}

/// <summary>
/// Exécuteur d'actions sur les fichiers.
/// Chaque action est implémentée de manière fonctionnelle et robuste.
/// Converti de static vers injectable (Amélioration #1/#2).
/// </summary>
public sealed class FileActionExecutor : IFileActionExecutor
{
    /// <inheritdoc/>
    public bool Execute(FileActionType actionType, string path)
    {
        try
        {
            return actionType switch
            {
                FileActionType.Open => OpenFile(path),
                FileActionType.OpenWith => OpenWith(path),
                FileActionType.OpenLocation => OpenLocation(path),
                FileActionType.CopyPath => CopyToClipboard(path),
                FileActionType.CopyName => CopyNameToClipboard(path),
                FileActionType.CopyUrl => CopyToClipboard(path),
                FileActionType.Compress => CompressToZip(path),
                FileActionType.SendByEmail => SendByEmail(path),
                FileActionType.Delete => DeleteFile(path),
                FileActionType.Properties => ShowProperties(path),
                FileActionType.RunAsAdmin => RunAsAdmin(path),
                FileActionType.OpenPrivate => OpenInPrivateMode(path),
                FileActionType.OpenInTerminal => OpenInTerminal(path),
                FileActionType.OpenInExplorer => OpenInExplorer(path),
                FileActionType.OpenInVSCode => OpenInVSCode(path),
                FileActionType.EditInEditor => EditInEditor(path),
                FileActionType.Rename => false, // Géré par l'UI
                FileActionType.Pin => false,    // Géré par le ViewModel
                FileActionType.Unpin => false,  // Géré par le ViewModel
                FileActionType.CreateAlias => false,  // Géré par l'UI
                FileActionType.DeleteAlias => false,  // Géré par le ViewModel
                _ => false
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileAction] Erreur {actionType}: {ex.Message}");
            return false;
        }
    }

    #region Ouverture

    private static bool OpenFile(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
        return true;
    }
    
    private static bool OpenWith(string path)
    {
        if (!File.Exists(path)) return false;
        
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"shell32.dll,OpenAs_RunDLL \"{path}\"",
            UseShellExecute = false
        });
        return true;
    }
    
    private static bool OpenLocation(string path)
    {
        if (File.Exists(path))
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
            return true;
        }
        
        if (Directory.Exists(path))
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
                return true;
            }
            Process.Start("explorer.exe", $"\"{path}\"");
            return true;
        }
        
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
            return true;
        }
        
        return false;
    }

    #endregion

    #region Presse-papiers

    private static bool CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        Clipboard.SetText(text);
        return true;
    }
    
    private static bool CopyNameToClipboard(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        
        var name = File.Exists(path) || Directory.Exists(path)
            ? Path.GetFileName(path)
            : path;
        
        if (string.IsNullOrEmpty(name)) return false;
        Clipboard.SetText(name);
        return true;
    }

    #endregion

    #region Opérations fichier

    private static bool CompressToZip(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;
        
        var baseName = Path.GetFileNameWithoutExtension(path);
        var parentDir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parentDir)) return false;
        
        var zipPath = Path.Combine(parentDir, $"{baseName}.zip");
        var counter = 1;
        while (File.Exists(zipPath))
        {
            zipPath = Path.Combine(parentDir, $"{baseName} ({counter}).zip");
            counter++;
        }
        
        if (File.Exists(path))
        {
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
        }
        else if (Directory.Exists(path))
        {
            ZipFile.CreateFromDirectory(path, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        }
        
        Process.Start("explorer.exe", $"/select,\"{zipPath}\"");
        return true;
    }
    
    private static bool SendByEmail(string path)
    {
        if (!File.Exists(path)) return false;
        
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"shell32.dll,ShellExec_RunDLL ?subject=&body=&attach=\"{path}\"",
                UseShellExecute = false
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            try
            {
                var fileName = Path.GetFileName(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"mailto:?subject={Uri.EscapeDataString(fileName)}&body={Uri.EscapeDataString($"Voir pièce jointe: {fileName}")}",
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
    
    private static bool DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        
        if (Directory.Exists(path))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        
        return false;
    }

    private static bool ShowProperties(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;
        
        var sei = new NativeMethods.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFO>(),
            lpVerb = "properties",
            lpFile = path,
            nShow = 1,
            fMask = 0x0000000C
        };
        
        return NativeMethods.ShellExecuteEx(ref sei);
    }

    #endregion

    #region Exécution

    private static bool RunAsAdmin(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            Verb = "runas"
        });
        return true;
    }

    private static bool OpenInPrivateMode(string url)
    {
        var defaultBrowser = GetDefaultBrowserExecutable();
        
        if (!string.IsNullOrEmpty(defaultBrowser))
        {
            var browserName = Path.GetFileNameWithoutExtension(defaultBrowser).ToLowerInvariant();
            var privateArg = browserName switch
            {
                "chrome" or "chromium" => "--incognito",
                "msedge" => "--inprivate",
                "firefox" => "-private-window",
                "brave" => "--incognito",
                "vivaldi" => "--incognito",
                "opera" => "--private",
                _ => "--inprivate"
            };
            
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = defaultBrowser,
                    Arguments = $"{privateArg} \"{url}\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch { /* Fallback ci-dessous */ }
        }
        
        string[] browsers = ["msedge.exe", "chrome.exe", "firefox.exe"];
        string[] args = ["--inprivate", "--incognito", "-private-window"];
        
        for (int i = 0; i < browsers.Length; i++)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = browsers[i],
                    Arguments = $"{args[i]} \"{url}\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch { continue; }
        }
        
        // Aucun navigateur n'a accepté le mode privé : signaler l'échec plutôt que d'ouvrir
        // l'adresse en navigation NORMALE, à l'opposé de ce que l'utilisateur a demandé.
        return false;
    }

    private static string? GetDefaultBrowserExecutable()
    {
        try
        {
            using var userChoice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            
            var progId = userChoice?.GetValue("ProgId") as string;
            if (string.IsNullOrEmpty(progId)) return null;
            
            using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            var commandLine = command?.GetValue(null) as string;
            if (string.IsNullOrEmpty(commandLine)) return null;
            
            if (commandLine.StartsWith('"'))
            {
                var endQuote = commandLine.IndexOf('"', 1);
                if (endQuote > 0)
                    return commandLine[1..endQuote];
            }
            
            return commandLine.Split(' ')[0];
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Terminal / Éditeurs

    private static bool OpenInTerminal(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
        
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "wt.exe", Arguments = $"-d \"{folder}\"", UseShellExecute = true });
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "powershell.exe", WorkingDirectory = folder, UseShellExecute = true });
                return true;
            }
            catch
            {
                Process.Start(new ProcessStartInfo { FileName = "cmd.exe", WorkingDirectory = folder, UseShellExecute = true });
                return true;
            }
        }
    }

    private static bool OpenInExplorer(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder)) return false;
        
        Process.Start("explorer.exe", $"\"{folder}\"");
        return true;
    }
    
    private static bool OpenInVSCode(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder)) return false;
        
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "code", Arguments = $"\"{folder}\"", UseShellExecute = true });
            return true;
        }
        catch { /* Pas dans le PATH */ }
        
        string[] possiblePaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Microsoft VS Code", "Code.exe"),
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            @"C:\Program Files (x86)\Microsoft VS Code\Code.exe"
        ];
        
        foreach (var codePath in possiblePaths)
        {
            if (!File.Exists(codePath)) continue;
            Process.Start(new ProcessStartInfo { FileName = codePath, Arguments = $"\"{folder}\"", UseShellExecute = true });
            return true;
        }
        
        return false;
    }
    
    private static bool EditInEditor(string path)
    {
        if (!File.Exists(path)) return false;
        
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "code", Arguments = $"\"{path}\"", UseShellExecute = true });
            return true;
        }
        catch { /* Pas dans le PATH */ }
        
        string[] notepadPlusPaths = [@"C:\Program Files\Notepad++\notepad++.exe", @"C:\Program Files (x86)\Notepad++\notepad++.exe"];
        
        foreach (var nppPath in notepadPlusPaths)
        {
            if (!File.Exists(nppPath)) continue;
            Process.Start(new ProcessStartInfo { FileName = nppPath, Arguments = $"\"{path}\"", UseShellExecute = true });
            return true;
        }
        
        Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
        return true;
    }

    #endregion
}
