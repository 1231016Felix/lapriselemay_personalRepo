using QuickLauncher.Models;
using QuickLauncher.Services;
using QuickLauncher.Services.CommandHandlers;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Clic (ou Entrée) sur une commande qui attend un argument, lancée sans :
/// son préfixe doit être écrit dans la barre de recherche (comme :translate)
/// au lieu de ne rien faire.
/// Seules des commandes sans effet de bord sont exécutées ici (pas de volume,
/// WiFi, etc.) : aucun appel système ni fenêtre WPF.
/// </summary>
public class SystemControlExecutorTests
{
    [Theory]
    [InlineData(":note", ":note ")]
    [InlineData(":find", ":find ")]
    [InlineData(":timer", ":timer ")]
    [InlineData(":process", ":process ")]
    [InlineData(":translate", ":translate ")]
    [InlineData(":ai", ":ai ")]
    public async Task Commande_sans_argument_ecrit_son_prefixe_dans_la_barre(string command, string expected)
    {
        var result = await CreateExecutor().ExecuteAsync(command);

        Assert.True(result.Handled);
        Assert.Equal(expected, result.AutoCompleteText);
        Assert.False(result.ShouldHide);
    }

    [Fact]
    public async Task Prefixe_personnalise_est_respecte()
    {
        var settings = new AppSettings();
        settings.SystemCommands.First(c => c.Type == SystemControlType.Note).Prefix = "n";

        var result = await CreateExecutor(settings).ExecuteAsync(":n");

        Assert.Equal(":n ", result.AutoCompleteText);
    }

    /// <summary>
    /// Avec un argument, rien ne change : ces commandes restent re-routées
    /// vers le CommandRouter par le ViewModel.
    /// </summary>
    [Theory]
    [InlineData(":translate hello")]
    [InlineData(":ai bonjour")]
    [InlineData(":find rapport")]
    public async Task Commande_avec_argument_n_est_pas_autocompletee(string command)
    {
        var result = await CreateExecutor().ExecuteAsync(command);

        Assert.False(result.Handled);
        Assert.Null(result.AutoCompleteText);
    }

    private static SystemControlExecutor CreateExecutor(AppSettings? settings = null)
    {
        var provider = new FakeSettingsProvider(settings ?? new AppSettings());
        return new SystemControlExecutor(provider, new NoteWidgetService(provider), new TimerWidgetService(provider));
    }

    private sealed class FakeSettingsProvider(AppSettings settings) : ISettingsProvider
    {
        public AppSettings Current { get; } = settings;
        public event EventHandler<AppSettings>? SettingsChanged { add { } remove { } }
        public void Save() { }
        public void Reload() { }
        public void Update(Action<AppSettings> updateAction) => updateAction(Current);
    }
}
