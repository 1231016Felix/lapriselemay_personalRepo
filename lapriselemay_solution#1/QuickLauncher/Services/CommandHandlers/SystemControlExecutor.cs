using QuickLauncher.Models;

namespace QuickLauncher.Services.CommandHandlers;

/// <summary>
/// Implémentation de <see cref="ISystemControlExecutor"/>.
/// Gère l'exécution des commandes système quand l'utilisateur valide (Entrée).
/// 
/// Découplé du ViewModel : ne connaît ni la fenêtre, ni le Dispatcher.
/// Retourne un résultat déclaratif que le ViewModel interprète.
/// </summary>
public sealed class SystemControlExecutor : ISystemControlExecutor
{
    private readonly ISettingsProvider _settingsProvider;
    private readonly NoteWidgetService _noteWidgetService;
    private readonly TimerWidgetService _timerWidgetService;
    
    private AppSettings Settings => _settingsProvider.Current;
    
    public SystemControlExecutor(
        ISettingsProvider settingsProvider,
        NoteWidgetService noteWidgetService,
        TimerWidgetService timerWidgetService)
    {
        _settingsProvider = settingsProvider;
        _noteWidgetService = noteWidgetService;
        _timerWidgetService = timerWidgetService;
    }
    
    public async Task<SystemControlExecutionResult> ExecuteAsync(string command)
    {
        if (string.IsNullOrEmpty(command))
            return SystemControlExecutionResult.NotHandled;
        
        // === Copier depuis :translate ===
        if (command.StartsWith(":translate:copy:"))
        {
            var text = command[":translate:copy:".Length..];
            return CopyToClipboard(text, "📋 Traduction copiée");
        }
        
        // === Copier depuis :ai ===
        if (command.StartsWith(":ai:copy:"))
        {
            var text = command[":ai:copy:".Length..];
            return CopyToClipboard(text, "📋 Réponse IA copiée");
        }
        
        // === Commandes configurables ===
        var parts = command.TrimStart(':').Split(' ', 2);
        var cmdPrefix = parts[0];
        var arg = parts.Length > 1 ? parts[1] : null;
        
        var matchedCmd = Settings.SystemCommands.FirstOrDefault(c =>
            c.IsEnabled && c.Prefix.Equals(cmdPrefix, StringComparison.OrdinalIgnoreCase));
        
        if (matchedCmd == null)
            return await ExecuteNormalizedCommandAsync(command);
        
        return matchedCmd.Type switch
        {
            SystemControlType.Weather or SystemControlType.Translate or SystemControlType.AiChat
                => HandleAsyncCommandExecution(matchedCmd, arg),
            
            SystemControlType.Timer => ExecuteTimer(arg),
            SystemControlType.Note => ExecuteNote(arg),
            SystemControlType.Screenshot => ExecuteScreenshot(arg),
            SystemControlType.DiskInfo => ExecuteDiskInfo(),
            SystemControlType.ProcessKill => ExecuteProcess(arg),
            
            // Commandes applicatives
            SystemControlType.AppSettings => new() { Handled = true, ShouldHide = true, AppAction = AppAction.OpenSettings },
            SystemControlType.AppQuit => new() { Handled = true, AppAction = AppAction.Quit },
            SystemControlType.AppReindex => new() { Handled = true, ShouldHide = true, AppAction = AppAction.Reindex },
            SystemControlType.AppHistory => new() { Handled = true, AppAction = AppAction.ShowHistory },
            SystemControlType.AppClearHistory => new() { Handled = true, ShouldHide = true, AppAction = AppAction.ClearHistory },
            SystemControlType.AppHelp => new() { Handled = true, AppAction = AppAction.ShowHelp },
            
            _ => await ExecuteNormalizedCommandAsync(command)
        };
    }
    
    /// <summary>
    /// Commandes async (météo, traduction, IA) : redirigent vers le CommandRouter.
    /// Si pas d'argument, autocomplete le préfixe dans la barre de recherche.
    /// </summary>
    private static SystemControlExecutionResult HandleAsyncCommandExecution(SystemControlCommand cmd, string? arg)
    {
        if (!string.IsNullOrEmpty(arg) || cmd.Type == SystemControlType.Weather)
        {
            // Le ViewModel doit re-router via CommandRouter (requête async)
            return SystemControlExecutionResult.NotHandled;
        }
        
        // Autocomplete : remplir la barre de recherche avec le préfixe
        return new SystemControlExecutionResult
        {
            Handled = true,
            AutoCompleteText = $":{cmd.Prefix} "
        };
    }
    
    private SystemControlExecutionResult ExecuteTimer(string? arg)
    {
        if (string.IsNullOrEmpty(arg))
            return SystemControlExecutionResult.NotHandled;
        
        var timerParts = arg.Split(' ', 2);
        var duration = timerParts[0];
        var label = timerParts.Length > 1 ? timerParts[1] : null;
        
        var timerWidget = _timerWidgetService.CreateWidget(duration, label);
        if (timerWidget != null)
        {
            var durationText = TimerWidgetService.FormatDuration(
                TimeSpan.FromSeconds(timerWidget.DurationSeconds));
            return new SystemControlExecutionResult
            {
                Handled = true,
                ShouldHide = true,
                ResultsToShow =
                [
                    new SearchResult
                    {
                        Name = $"⏱️ Minuterie créée: {durationText}",
                        Description = timerWidget.Label,
                        Type = ResultType.SystemControl,
                        DisplayIcon = "ℹ️"
                    }
                ]
            };
        }
        
        return new SystemControlExecutionResult
        {
            Handled = true,
            ResultsToShow =
            [
                new SearchResult
                {
                    Name = "❌ Format invalide",
                    Description = "Utilisez: 5m, 30s, 1h, 1h30m, etc.",
                    Type = ResultType.SystemControl,
                    DisplayIcon = "❌"
                }
            ]
        };
    }
    
    private SystemControlExecutionResult ExecuteNote(string? arg)
    {
        if (string.IsNullOrEmpty(arg))
            return SystemControlExecutionResult.NotHandled;
        
        var widgetInfo = _noteWidgetService.CreateWidget(arg);
        return new SystemControlExecutionResult
        {
            Handled = true,
            ShouldHide = true,
            ResultsToShow =
            [
                new SearchResult
                {
                    Name = "📝 Note créée!",
                    Description = widgetInfo.Content.Length > 50 ? widgetInfo.Content[..47] + "..." : widgetInfo.Content,
                    Type = ResultType.SystemControl,
                    DisplayIcon = "ℹ️"
                }
            ]
        };
    }
    
    private static SystemControlExecutionResult ExecuteScreenshot(string? arg)
    {
        if (arg is "snip" or "region" or "select")
        {
            return new SystemControlExecutionResult
            {
                Handled = true,
                ShouldHide = true,
                ScreenCaptureMode = arg
            };
        }
        
        // Capture plein écran — le ViewModel gère l'aspect async/UI
        return new SystemControlExecutionResult
        {
            Handled = true,
            ShouldHide = true,
            ScreenCaptureMode = "fullscreen"
        };
    }
    
    private static SystemControlExecutionResult ExecuteDiskInfo()
    {
        var disks = SystemControlService.GetDiskInfo();
        if (disks.Count == 0)
            return new SystemControlExecutionResult
            {
                Handled = true,
                ResultsToShow = [new SearchResult
                {
                    Name = "❌ Aucun lecteur détecté",
                    Type = ResultType.SystemControl, DisplayIcon = "❌"
                }]
            };

        var results = disks.Select(d =>
        {
            var bar = new string('█', d.UsedPercent / 10) + new string('░', 10 - d.UsedPercent / 10);
            return new SearchResult
            {
                Name = $"💾 {d.Name}  {bar}  {d.UsedPercent}%",
                Description = $"{d.FreeGB} Go libre sur {d.TotalGB} Go ({d.UsedGB} Go utilisé)",
                Type = ResultType.SystemControl,
                DisplayIcon = d.UsedPercent >= 90 ? "🔴" : d.UsedPercent >= 70 ? "🟡" : "🟢"
            };
        }).ToList();

        return new SystemControlExecutionResult { Handled = true, ResultsToShow = results };
    }

    private static SystemControlExecutionResult ExecuteProcess(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return new SystemControlExecutionResult
            {
                Handled = true,
                ResultsToShow = [new SearchResult
                {
                    Name = "Usage: :process kill <nom> ou :process list <nom>",
                    Description = "Tuer ou lister des processus par nom",
                    Type = ResultType.SystemControl, DisplayIcon = "ℹ️"
                }]
            };

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCmd = parts[0].ToLowerInvariant();
        var processName = parts.Length > 1 ? parts[1] : null;

        if (subCmd is "kill" or "stop" or "end" && !string.IsNullOrWhiteSpace(processName))
        {
            var (success, killed, message) = SystemControlService.KillProcessByName(processName);
            return new SystemControlExecutionResult
            {
                Handled = true,
                ResultsToShow = [new SearchResult
                {
                    Name = success ? $"💀 {message}" : $"❌ {message}",
                    Description = success ? "Processus terminé" : "Échec",
                    Type = ResultType.SystemControl,
                    DisplayIcon = success ? "✅" : "❌"
                }]
            };
        }

        if (subCmd is "list" or "find" && !string.IsNullOrWhiteSpace(processName))
        {
            var procs = SystemControlService.FindProcesses(processName);
            if (procs.Length == 0)
            {
                foreach (var p in procs) p.Dispose();
                return new SystemControlExecutionResult
                {
                    Handled = true,
                    ResultsToShow = [new SearchResult
                    {
                        Name = $"Aucun processus correspondant à \"{processName}\"",
                        Type = ResultType.SystemControl, DisplayIcon = "❌"
                    }]
                };
            }

            var results = procs
                .GroupBy(p => { try { return p.ProcessName; } catch { return "?"; } })
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g =>
                {
                    long memMB = 0;
                    try { memMB = g.First().WorkingSet64 / (1024 * 1024); } catch { }
                    return new SearchResult
                    {
                        Name = $"🔹 {g.Key}",
                        Description = g.Count() == 1
                            ? $"1 instance • ~{memMB} Mo RAM"
                            : $"{g.Count()} instances • ~{memMB} Mo RAM (chacun)",
                        Type = ResultType.SystemControl,
                        DisplayIcon = "📊"
                    };
                }).ToList();

            foreach (var p in procs) p.Dispose();
            return new SystemControlExecutionResult { Handled = true, ResultsToShow = results };
        }

        // Raccourci : si l'arg est juste un nom (sans kill/list), traiter comme kill
        var directResult = SystemControlService.KillProcessByName(arg);
        return new SystemControlExecutionResult
        {
            Handled = true,
            ResultsToShow = [new SearchResult
            {
                Name = directResult.Success ? $"💀 {directResult.Message}" : $"❌ {directResult.Message}",
                Description = directResult.Success ? "Processus terminé" : "Échec",
                Type = ResultType.SystemControl,
                DisplayIcon = directResult.Success ? "✅" : "❌"
            }]
        };
    }
    
    /// <summary>
    /// Normalise la commande depuis le préfixe personnalisé vers le format standard
    /// attendu par SystemControlService, puis exécute.
    /// </summary>
    private async Task<SystemControlExecutionResult> ExecuteNormalizedCommandAsync(string command)
    {
        var normalized = NormalizeSystemCommand(command);
        var result = await SystemControlService.ExecuteCommandAsync(normalized);
        
        if (result == null)
            return SystemControlExecutionResult.NotHandled;
        
        var results = new List<SearchResult>
        {
            new()
            {
                Name = result.Message,
                Description = result.Success ? "Commande exécutée" : "Erreur",
                Type = ResultType.SystemControl,
                DisplayIcon = result.Success ? "✅" : "❌"
            }
        };
        
        if (result.Success && !string.IsNullOrEmpty(result.FilePath))
        {
            results.Add(new SearchResult
            {
                Name = "Ouvrir la capture",
                Description = result.FilePath,
                Type = ResultType.File,
                Path = result.FilePath,
                DisplayIcon = "📂"
            });
        }
        
        // Certaines commandes ferment la fenêtre après exécution
        var commandLower = normalized.ToLowerInvariant();
        var shouldHide = result.Success && IsAutoHideCommand(commandLower);
        
        return new SystemControlExecutionResult
        {
            Handled = true,
            ShouldHide = shouldHide,
            ResultsToShow = results
        };
    }
    
    /// <summary>
    /// Convertit une commande avec préfixe personnalisé vers le format standard.
    /// </summary>
    internal string NormalizeSystemCommand(string command)
    {
        var parts = command.TrimStart(':').Split(' ', 2);
        var prefix = parts[0];
        var arg = parts.Length > 1 ? parts[1] : null;
        
        var matchedCmd = Settings.SystemCommands.FirstOrDefault(c =>
            c.IsEnabled && c.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase));
        
        if (matchedCmd == null)
            return command;
        
        var standardCmd = matchedCmd.Type switch
        {
            SystemControlType.Volume => "volume",
            SystemControlType.Mute => "mute",
            SystemControlType.Brightness => "brightness",
            SystemControlType.Wifi => "wifi",
            SystemControlType.Lock => "lock",
            SystemControlType.Sleep => "sleep",
            SystemControlType.Hibernate => "hibernate",
            SystemControlType.Shutdown => "shutdown",
            SystemControlType.Restart => "restart",
            SystemControlType.Screenshot => "screenshot",
            SystemControlType.Logoff => "logoff",
            SystemControlType.EmptyRecycleBin => "emptybin",
            SystemControlType.OpenTaskManager => "taskmgr",
            SystemControlType.OpenWindowsSettings => "winsettings",
            SystemControlType.OpenControlPanel => "control",
            SystemControlType.EmptyTemp => "emptytemp",
            SystemControlType.OpenCmdAdmin => "cmd",
            SystemControlType.OpenPowerShellAdmin => "powershell",
            SystemControlType.RestartExplorer => "restartexplorer",
            SystemControlType.FlushDns => "flushdns",
            SystemControlType.OpenStartupFolder => "startup",
            SystemControlType.OpenHostsFile => "hosts",
            SystemControlType.ProcessKill => "process",
            SystemControlType.DiskInfo => "disk",
            _ => prefix
        };
        
        return string.IsNullOrEmpty(arg) ? $":{standardCmd}" : $":{standardCmd} {arg}";
    }
    
    private static SystemControlExecutionResult CopyToClipboard(string text, string notification)
    {
        if (string.IsNullOrEmpty(text))
            return SystemControlExecutionResult.NotHandled;
        
        System.Windows.Clipboard.SetText(text);
        return new SystemControlExecutionResult
        {
            Handled = true,
            ShouldHide = true,
            Notification = notification
        };
    }
    
    private static bool IsAutoHideCommand(string normalizedCommand)
    {
        return normalizedCommand.Contains("sleep") || normalizedCommand.Contains("lock") ||
               normalizedCommand.Contains("shutdown") || normalizedCommand.Contains("restart") ||
               normalizedCommand.Contains("hibernate") || normalizedCommand.Contains("logoff") ||
               normalizedCommand.Contains("restartexplorer");
    }
}
