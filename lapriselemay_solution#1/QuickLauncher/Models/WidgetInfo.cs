namespace QuickLauncher.Models;

/// <summary>
/// Note rapide de l'utilisateur.
/// </summary>
public sealed class NoteItem
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Preview => Content.Length > 50 ? Content[..47] + "..." : Content;
    public string DateFormatted => CreatedAt.ToString("dd/MM/yyyy HH:mm");
}

/// <summary>
/// État persisté d'un widget de note sur le bureau.
/// </summary>
public sealed class NoteWidgetInfo
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public double Left { get; set; }
    public double Top { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Copie pour le clone-swap du SettingsProvider (les snapshots ne partagent rien).</summary>
    public NoteWidgetInfo Clone() => (NoteWidgetInfo)MemberwiseClone();
}

/// <summary>
/// État persisté d'un widget de minuterie sur le bureau.
/// </summary>
public sealed class TimerWidgetInfo
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }

    /// <summary>Temps restant figé pendant une pause (secondes).</summary>
    public int RemainingSeconds { get; set; }

    public double Left { get; set; }
    public double Top { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Minuterie en pause : <see cref="RemainingSeconds"/> fait foi.</summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Échéance UTC quand la minuterie tourne. Absente pour les entrées antérieures à ce
    /// champ : repli sur création + durée totale.
    /// </summary>
    public DateTime? EndsAtUtc { get; set; }

    /// <summary>
    /// Temps restant à l'instant donné : figé si la minuterie est en pause, sinon calculé
    /// depuis l'échéance persistée. Pur (l'horloge est injectée) pour être testable.
    /// </summary>
    public TimeSpan GetRemaining(DateTime nowUtc)
    {
        if (IsPaused)
            return TimeSpan.FromSeconds(Math.Max(0, RemainingSeconds));

        var endsAt = EndsAtUtc ?? CreatedAt.ToUniversalTime().AddSeconds(DurationSeconds);
        return endsAt - nowUtc;
    }

    /// <summary>Copie pour le clone-swap du SettingsProvider (les snapshots ne partagent rien).</summary>
    public TimerWidgetInfo Clone() => (TimerWidgetInfo)MemberwiseClone();
}