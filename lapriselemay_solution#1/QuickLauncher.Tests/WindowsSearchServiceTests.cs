using QuickLauncher.Services;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Échappement des requêtes envoyées à Windows Search (SQL Search.CollatorDSO).
/// L'ordre des remplacements est le point sensible : '[' doit être échappé en
/// premier, sinon les crochets introduits par % et _ sont re-échappés et le
/// joker redevient actif.
/// </summary>
public class WindowsSearchServiceTests
{
    [Theory]
    [InlineData("a%b", "a[%]b")]
    [InlineData("a[b", "a[[]b")]
    [InlineData("a_b", "a[_]b")]
    [InlineData("a'b", "a''b")]
    public void EscapeQuery_echappe_les_caracteres_speciaux(string input, string expected)
    {
        Assert.Equal(expected, WindowsSearchService.EscapeQuery(input));
    }

    [Fact]
    public void EscapeQuery_laisse_intact_un_texte_ordinaire()
    {
        Assert.Equal("notepad", WindowsSearchService.EscapeQuery("notepad"));
        Assert.Equal("mon fichier.txt", WindowsSearchService.EscapeQuery("mon fichier.txt"));
    }

    [Fact]
    public void EscapeQuery_supprime_les_guillemets_doubles()
    {
        Assert.Equal("abc", WindowsSearchService.EscapeQuery("\"abc\""));
    }

    /// <summary>
    /// Régression : les crochets introduits par l'échappement de % et de _ ne
    /// doivent PAS être ré-échappés. Si l'ordre des Replace était inversé,
    /// "a%b" deviendrait "a[[]%]b" et le % redeviendrait un joker SQL.
    /// </summary>
    [Fact]
    public void EscapeQuery_n_echappe_pas_deux_fois_les_crochets_introduits()
    {
        Assert.Equal("a[%]b", WindowsSearchService.EscapeQuery("a%b"));
        Assert.Equal("a[_]b", WindowsSearchService.EscapeQuery("a_b"));
        Assert.Equal("[%][_]", WindowsSearchService.EscapeQuery("%_"));
    }

    [Fact]
    public void EscapeQuery_gere_les_combinaisons()
    {
        // '[' échappé en premier, puis % et _, puis l'apostrophe doublée.
        Assert.Equal("[[][%][_]''", WindowsSearchService.EscapeQuery("[%_'"));
    }

    [Fact]
    public void EscapeQuery_gere_la_chaine_vide()
    {
        Assert.Equal(string.Empty, WindowsSearchService.EscapeQuery(string.Empty));
    }
}
