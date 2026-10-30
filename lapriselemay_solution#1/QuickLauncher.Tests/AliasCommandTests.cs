using QuickLauncher.Services;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Cibles d'alias : jamais confiées brutes à cmd.exe ; chemins avec espaces, accents et
/// « &amp; » restent des chemins. Aucun processus n'est lancé (existence simulée).
/// </summary>
public class AliasCommandTests
{
    private static readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Program Files\Mon application\App.exe",
        @"C:\Données\R&D\outil.exe",
        @"C:\Clients\Dossier été"
    };

    private static (string File, string? Args) Parse(string target)
        => AliasCommand.Parse(target, Existing.Contains);

    [Fact]
    public void Chemin_existant_avec_espaces_est_la_cible_entiere()
        => Assert.Equal((@"C:\Program Files\Mon application\App.exe", (string?)null), Parse(@"C:\Program Files\Mon application\App.exe"));

    [Fact]
    public void Esperluette_dans_un_chemin_n_est_pas_un_operateur()
        => Assert.Equal((@"C:\Données\R&D\outil.exe", (string?)null), Parse(@"C:\Données\R&D\outil.exe"));

    [Fact]
    public void Dossier_accentue_avec_espaces()
        => Assert.Equal((@"C:\Clients\Dossier été", (string?)null), Parse(@"C:\Clients\Dossier été"));

    [Fact]
    public void Executable_entre_guillemets_suivi_d_arguments()
        => Assert.Equal((@"C:\x y\a.exe", "--flag \"b c\""), Parse("\"C:\\x y\\a.exe\" --flag \"b c\""));

    [Fact]
    public void Chemin_non_protege_suivi_d_arguments()
        => Assert.Equal((@"C:\Program Files\Mon application\App.exe", "--new-window"), Parse(@"C:\Program Files\Mon application\App.exe --new-window"));

    [Fact]
    public void Programme_nu_et_arguments()
        => Assert.Equal(("code", @"C:\projet"), Parse(@"code C:\projet"));

    [Theory]
    [InlineData("ms-settings:")]
    [InlineData("ms-settings:display")]
    [InlineData("https://example.com/a b")]
    public void Uri_est_transmise_telle_quelle(string uri)
        => Assert.Equal((uri, (string?)null), Parse(uri));

    [Theory]
    [InlineData(">ipconfig /all | clip", true)]
    [InlineData("  > dir", true)]
    [InlineData(@"C:\a.exe", false)]
    public void Commande_shell_uniquement_avec_le_prefixe_explicite(string target, bool expected)
        => Assert.Equal(expected, AliasCommand.IsShellCommand(target));
}
