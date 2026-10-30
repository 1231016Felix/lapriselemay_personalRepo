using System.IO;
using System.Windows;
using QuickLauncher.Models;
using QuickLauncher.Services;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Restauration des minuteries (pause comprise), positions multi-écran et aperçus bornés.
/// </summary>
public class WidgetAndScreenTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Minuterie_en_pause_garde_son_temps_restant()
    {
        var timer = new TimerWidgetInfo { DurationSeconds = 600, RemainingSeconds = 120, IsPaused = true,
            CreatedAt = Now.AddHours(-5).ToLocalTime() };
        Assert.Equal(TimeSpan.FromSeconds(120), timer.GetRemaining(Now));
    }

    [Fact]
    public void Minuterie_en_marche_suit_son_echeance_UTC()
    {
        var timer = new TimerWidgetInfo { DurationSeconds = 600, EndsAtUtc = Now.AddMinutes(3) };
        Assert.Equal(TimeSpan.FromMinutes(3), timer.GetRemaining(Now));
    }

    [Fact]
    public void Ancienne_minuterie_sans_echeance_repli_sur_creation_plus_duree()
    {
        var timer = new TimerWidgetInfo { DurationSeconds = 600, CreatedAt = Now.AddMinutes(-4).ToLocalTime() };
        Assert.Equal(TimeSpan.FromMinutes(6), timer.GetRemaining(Now));
    }

    [Fact]
    public void Position_sur_un_ecran_secondaire_a_gauche_est_acceptee()
    {
        Rect[] screens = [new Rect(0, 0, 1920, 1040), new Rect(-1920, 0, 1920, 1040)];
        Assert.True(MonitorHelper.FitsInAnyWorkArea(-1500, 200, 700, 400, screens));
        Assert.False(MonitorHelper.FitsInAnyWorkArea(-4000, 200, 700, 400, screens));
        Assert.Equal(screens[1], MonitorHelper.FindWorkAreaContaining(-1500, 200, screens));
    }

    [Fact]
    public async Task Apercu_texte_borne_meme_avec_une_ligne_geante()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ql_preview_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, new string('x', 1_000_000));
            var preview = await FilePreviewService.GeneratePreviewAsync(path);

            Assert.NotNull(preview);
            Assert.True(preview!.IsTruncated);
            Assert.True(preview.TextContent!.Length < 3_200, $"{preview.TextContent.Length} caractères");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Apercu_annule_ne_produit_rien()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await FilePreviewService.GeneratePreviewAsync(Path.GetTempPath(), cts.Token));
    }
}
