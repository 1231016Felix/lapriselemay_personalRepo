using System.Text.RegularExpressions;
using System.Windows;
using QuickLauncher.Models;
using QuickLauncher.Views;

namespace QuickLauncher.Services;

/// <summary>
/// Service de gestion des widgets de minuterie sur le bureau.
/// Utilise ISettingsProvider pour éviter les lectures disque répétées.
/// </summary>
public sealed partial class TimerWidgetService
{
    private readonly Dictionary<int, TimerWidget> _activeWidgets = [];
    private readonly ISettingsProvider _settingsProvider;
    private readonly object _lock = new();
    private int _nextId = 1;
    
    private AppSettings Settings => _settingsProvider.Current;
    
    public TimerWidgetService(ISettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
    }
    
    /// <summary>
    /// Crée un nouveau widget de minuterie.
    /// </summary>
    public TimerWidgetInfo? CreateWidget(string duration, string? label = null)
    {
        var parsedDuration = ParseDuration(duration);
        if (parsedDuration == null) return null;
        
        lock (_lock)
        {
            var settings = Settings;
            
            // Trouver le prochain ID disponible
            while (settings.Integrations.TimerWidgets.Any(w => w.Id == _nextId) || _activeWidgets.ContainsKey(_nextId))
                _nextId++;
            
            var timerInfo = new TimerWidgetInfo
            {
                Id = _nextId++,
                Label = label ?? "Minuterie",
                DurationSeconds = (int)parsedDuration.Value.TotalSeconds,
                RemainingSeconds = (int)parsedDuration.Value.TotalSeconds,
                CreatedAt = DateTime.Now,
                EndsAtUtc = DateTime.UtcNow + parsedDuration.Value
            };
            
            // Calculer la position (décalage pour plusieurs widgets)
            var workArea = SystemParameters.WorkArea;
            var widgetIndex = _activeWidgets.Count;
            timerInfo.Left = workArea.Right - 180 - (widgetIndex * 35);
            timerInfo.Top = workArea.Bottom - 120 - (widgetIndex * 35);
            
            // Créer et afficher le widget
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var widget = new TimerWidget(
                    timerInfo.Id,
                    timerInfo.Label,
                    parsedDuration.Value,
                    OnWidgetClosed,
                    OnTimerCompleted,
                    SaveWidgetPosition,
                    SavePauseState
                );
                widget.SetPosition(timerInfo.Left, timerInfo.Top);
                widget.Show();
                
                _activeWidgets[timerInfo.Id] = widget;
            });
            
            // Sauvegarder
            _settingsProvider.Update(s => s.Integrations.TimerWidgets.Add(timerInfo));
            
            return timerInfo;
        }
    }
    
    /// <summary>
    /// Callback quand un widget est fermé (annulé).
    /// </summary>
    private void OnWidgetClosed(int timerId)
    {
        lock (_lock)
        {
            _activeWidgets.Remove(timerId);
            _settingsProvider.Update(s => s.Integrations.TimerWidgets.RemoveAll(w => w.Id == timerId));
        }
    }
    
    /// <summary>
    /// Callback quand un timer est terminé.
    /// </summary>
    private void OnTimerCompleted(int timerId)
    {
        // Jouer un son de notification
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                System.Media.SystemSounds.Exclamation.Play();
            }
            catch { }
        });
        
        // Supprimer des settings (mais garder le widget visible)
        lock (_lock)
        {
            _settingsProvider.Update(s => s.Integrations.TimerWidgets.RemoveAll(w => w.Id == timerId));
        }
    }
    
    /// <summary>
    /// Persiste une mise en pause ou une reprise : en pause, le temps restant est figé ;
    /// en marche, l'échéance UTC est recalculée depuis le temps restant.
    /// </summary>
    public void SavePauseState(int timerId, bool isPaused, TimeSpan remaining)
    {
        _settingsProvider.Update(s =>
        {
            var timer = s.Integrations.TimerWidgets.FirstOrDefault(w => w.Id == timerId);
            if (timer == null) return;

            timer.IsPaused = isPaused;
            timer.RemainingSeconds = (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
            timer.EndsAtUtc = isPaused ? null : DateTime.UtcNow + remaining;
        });
    }

    /// <summary>
    /// Sauvegarde la position d'un widget.
    /// </summary>
    public void SaveWidgetPosition(int timerId, double left, double top)
    {
        _settingsProvider.Update(s =>
        {
            var timer = s.Integrations.TimerWidgets.FirstOrDefault(w => w.Id == timerId);
            if (timer != null)
            {
                timer.Left = left;
                timer.Top = top;
            }
        });
    }
    
    /// <summary>
    /// Restaure les widgets au démarrage de l'application.
    /// </summary>
    public void RestoreWidgets()
    {
        var expiredTimers = new List<int>();
        
        foreach (var timerInfo in Settings.Integrations.TimerWidgets.ToList())
        {
            // Temps restant depuis l'état persisté (pause comprise), et non plus depuis
            // la seule date de création : une pause passée ou en cours était ignorée.
            var remaining = timerInfo.GetRemaining(DateTime.UtcNow);

            if (remaining <= TimeSpan.Zero)
            {
                // Timer expiré pendant que l'app était fermée
                expiredTimers.Add(timerInfo.Id);
                continue;
            }
            
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var widget = new TimerWidget(
                    timerInfo.Id,
                    timerInfo.Label,
                    TimeSpan.FromSeconds(timerInfo.DurationSeconds),
                    OnWidgetClosed,
                    OnTimerCompleted,
                    SaveWidgetPosition,
                    SavePauseState
                );
                widget.SetPosition(timerInfo.Left, timerInfo.Top);
                widget.RestoreState(remaining, timerInfo.IsPaused);
                widget.Show();
                
                lock (_lock)
                {
                    _activeWidgets[timerInfo.Id] = widget;
                    if (timerInfo.Id >= _nextId)
                        _nextId = timerInfo.Id + 1;
                }
            });
        }
        
        // Nettoyer les timers expirés
        if (expiredTimers.Count > 0)
        {
            _settingsProvider.Update(s => s.Integrations.TimerWidgets.RemoveAll(w => expiredTimers.Contains(w.Id)));
        }
    }
    
    /// <summary>
    /// Ferme tous les widgets.
    /// </summary>
    public void CloseAll()
    {
        lock (_lock)
        {
            foreach (var widget in _activeWidgets.Values.ToList())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try { widget.Close(); } catch { }
                });
            }
            _activeWidgets.Clear();
        }
    }
    
    /// <summary>
    /// Parse une durée au format "5m", "30s", "1h", "1h30m", etc.
    /// </summary>
    public static TimeSpan? ParseDuration(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        
        input = input.Trim().ToLowerInvariant();
        
        // Format simple: juste un nombre = minutes
        if (int.TryParse(input, out var justMinutes))
            return TimeSpan.FromMinutes(justMinutes);
        
        // Patterns: 5m, 30s, 1h, 1h30m, 1h30, etc.
        var regex = DurationRegex();
        var match = regex.Match(input);
        
        if (!match.Success) return null;
        
        var hours = 0;
        var minutes = 0;
        var seconds = 0;
        
        if (match.Groups["hours"].Success)
            hours = int.Parse(match.Groups["hours"].Value);
        if (match.Groups["minutes"].Success)
            minutes = int.Parse(match.Groups["minutes"].Value);
        if (match.Groups["seconds"].Success)
            seconds = int.Parse(match.Groups["seconds"].Value);
        
        // Cas spécial: "1h30" sans "m" = 1h30m
        if (match.Groups["hourmin"].Success)
            minutes = int.Parse(match.Groups["hourmin"].Value);
        
        var total = new TimeSpan(hours, minutes, seconds);
        return total > TimeSpan.Zero ? total : null;
    }
    
    /// <summary>
    /// Formate une durée pour l'affichage.
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h{duration.Minutes:D2}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m{duration.Seconds:D2}s";
        return $"{duration.Seconds}s";
    }
    
    [GeneratedRegex(@"^(?:(?<hours>\d+)h)?(?:(?<minutes>\d+)m)?(?:(?<seconds>\d+)s)?(?:(?<hourmin>\d+)(?=\s|$))?$")]
    private static partial Regex DurationRegex();
}
