using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickLauncher.Services;

namespace QuickLauncher.Views;

/// <summary>
/// Widget de minuterie flottant sur le bureau.
/// </summary>
public partial class TimerWidget : Window
{
    private readonly int _timerId;
    private readonly TimeSpan _totalDuration;
    private readonly Action<int>? _onClose;
    private readonly Action<int>? _onCompleted;
    private readonly Action<int, double, double>? _onPositionChanged;
    private readonly Action<int, bool, TimeSpan>? _onPauseChanged;
    private readonly DispatcherTimer _updateTimer;
    
    private DateTime _endsAt;
    private TimeSpan _pausedRemaining;
    private bool _isPaused;
    private bool _isCompleted;
    
    public int TimerId => _timerId;
    public string Label { get; }
    
    public TimerWidget(int timerId, string label, TimeSpan duration, Action<int>? onClose = null, Action<int>? onCompleted = null, Action<int, double, double>? onPositionChanged = null, Action<int, bool, TimeSpan>? onPauseChanged = null)
    {
        InitializeComponent();
        
        _timerId = timerId;
        _totalDuration = duration;
        _onClose = onClose;
        _onCompleted = onCompleted;
        _onPositionChanged = onPositionChanged;
        _onPauseChanged = onPauseChanged;
        Label = label;

        // Horloge UTC : insensible aux changements d'heure (DST)
        _endsAt = DateTime.UtcNow + duration;
        
        LabelText.Text = string.IsNullOrEmpty(label) ? "⏱️ Timer" : $"⏱️ {label}";
        
        // Timer pour mise à jour de l'affichage
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _updateTimer.Tick += UpdateDisplay;
        _updateTimer.Start();
        
        // Position par défaut (bas droite)
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - 180;
        Top = workArea.Bottom - 120;
        
        // Attacher au bureau pour que le timer reste sur le bureau
        DesktopAttachHelper.AttachToDesktop(this);
        
        UpdateDisplay(null, EventArgs.Empty);
    }
    
    private void UpdateDisplay(object? sender, EventArgs e)
    {
        if (_isCompleted) return;
        
        TimeSpan remaining;
        
        if (_isPaused)
        {
            remaining = _pausedRemaining;
        }
        else
        {
            remaining = _endsAt - DateTime.UtcNow;
        }
        
        if (remaining <= TimeSpan.Zero)
        {
            // Timer terminé!
            _isCompleted = true;
            _updateTimer.Stop();
            
            TimeDisplay.Text = "00:00";
            TimeDisplay.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF6B6B"));
            ProgressBar.Width = 0;
            
            // Notification
            _onCompleted?.Invoke(_timerId);
            
            // Clignotement visuel
            StartCompletionAnimation();
            return;
        }
        
        // Affichage du temps
        if (remaining.TotalHours >= 1)
        {
            TimeDisplay.Text = $"{(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }
        else
        {
            TimeDisplay.Text = $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }
        
        // Barre de progression
        var progress = remaining.TotalMilliseconds / _totalDuration.TotalMilliseconds;
        var parentWidth = ((FrameworkElement)ProgressBar.Parent).ActualWidth;
        if (parentWidth > 0)
        {
            ProgressBar.Width = parentWidth * progress;
        }
        
        // Couleur selon le temps restant
        if (remaining.TotalSeconds <= 10)
        {
            TimeDisplay.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF6B6B"));
            ProgressBar.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF6B6B"));
        }
        else if (remaining.TotalSeconds <= 30)
        {
            TimeDisplay.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFB347"));
            ProgressBar.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFB347"));
        }
    }
    
    private void StartCompletionAnimation()
    {
        var blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var blinkCount = 0;
        
        blinkTimer.Tick += (s, e) =>
        {
            blinkCount++;
            TimeDisplay.Opacity = TimeDisplay.Opacity < 1 ? 1 : 0.3;
            
            if (blinkCount >= 10) // 5 secondes de clignotement
            {
                blinkTimer.Stop();
                TimeDisplay.Opacity = 1;
            }
        };
        
        blinkTimer.Start();
    }
    
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Source is System.Windows.Controls.Button)
            return;
            
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
            _onPositionChanged?.Invoke(_timerId, Left, Top);
        }
    }
    
    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        
        if (_isCompleted) return;
        
        if (_isPaused)
        {
            // Reprendre
            _endsAt = DateTime.UtcNow + _pausedRemaining;
            _isPaused = false;
            PauseButton.Content = "⏸";
            PauseButton.ToolTip = "Pause";
            _updateTimer.Start();
            _onPauseChanged?.Invoke(_timerId, false, _pausedRemaining);
        }
        else
        {
            // Pause
            _pausedRemaining = _endsAt - DateTime.UtcNow;
            _isPaused = true;
            PauseButton.Content = "▶";
            PauseButton.ToolTip = "Reprendre";
            _updateTimer.Stop();
            // Persister la pause : au redémarrage, la minuterie revient en pause avec ce temps
            _onPauseChanged?.Invoke(_timerId, true, _pausedRemaining);
        }
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _updateTimer.Stop();
        _onClose?.Invoke(_timerId);
        Close();
    }
    
    public void SetPosition(double left, double top)
    {
        Left = left;
        Top = top;
    }
    
    /// <summary>
    /// Restaure un timer avec le temps restant.
    /// </summary>
    public void RestoreWithRemaining(TimeSpan remaining)
    {
        _endsAt = DateTime.UtcNow + remaining;
        UpdateDisplay(null, EventArgs.Empty);
    }

    /// <summary>
    /// Restaure l'état persisté : temps restant et, le cas échéant, la pause.
    /// </summary>
    public void RestoreState(TimeSpan remaining, bool isPaused)
    {
        if (!isPaused)
        {
            RestoreWithRemaining(remaining);
            return;
        }

        _pausedRemaining = remaining;
        _isPaused = true;
        PauseButton.Content = "▶";
        PauseButton.ToolTip = "Reprendre";
        UpdateDisplay(null, EventArgs.Empty);
        _updateTimer.Stop();
    }
    
    protected override void OnClosed(EventArgs e)
    {
        _updateTimer.Stop();
        base.OnClosed(e);
    }
}
