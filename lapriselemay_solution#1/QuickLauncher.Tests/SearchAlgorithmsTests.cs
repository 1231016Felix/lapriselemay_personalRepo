using QuickLauncher.Services;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Distance de Damerau-Levenshtein (cœur du fuzzy matching) et nettoyage des
/// emojis appliqué aux libellés avant comparaison.
/// </summary>
public class SearchAlgorithmsTests
{
    // ---------- ComputeDamerauLevenshtein ----------

    [Fact]
    public void Distance_faute_de_frappe_courante_vaut_un()
    {
        // « firfox » → « firefox » : une seule insertion.
        Assert.Equal(1, SearchAlgorithms.ComputeDamerauLevenshtein("firfox", "firefox"));
    }

    [Fact]
    public void Distance_chaines_identiques_vaut_zero()
    {
        Assert.Equal(0, SearchAlgorithms.ComputeDamerauLevenshtein("notepad", "notepad"));
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    public void Distance_gere_les_chaines_vides(string a, string b, int expected)
    {
        Assert.Equal(expected, SearchAlgorithms.ComputeDamerauLevenshtein(a, b));
    }

    [Fact]
    public void Distance_substitution_simple_vaut_un()
    {
        Assert.Equal(1, SearchAlgorithms.ComputeDamerauLevenshtein("chat", "chut"));
    }

    [Fact]
    public void Distance_suppression_simple_vaut_un()
    {
        Assert.Equal(1, SearchAlgorithms.ComputeDamerauLevenshtein("chats", "chat"));
    }

    /// <summary>
    /// C'est le « Damerau » de Damerau-Levenshtein : une transposition de deux
    /// caractères adjacents coûte 1 et non 2. Sans ce cas, on aurait la simple
    /// distance de Levenshtein.
    /// </summary>
    [Theory]
    [InlineData("ca", "ac")]
    [InlineData("teh", "the")]
    [InlineData("excle", "excel")]
    public void Distance_transposition_adjacente_vaut_un(string a, string b)
    {
        Assert.Equal(1, SearchAlgorithms.ComputeDamerauLevenshtein(a, b));
    }

    [Fact]
    public void Distance_est_symetrique()
    {
        Assert.Equal(
            SearchAlgorithms.ComputeDamerauLevenshtein("firfox", "firefox"),
            SearchAlgorithms.ComputeDamerauLevenshtein("firefox", "firfox"));
    }

    [Fact]
    public void Distance_entre_chaines_sans_rapport_vaut_la_longueur_max()
    {
        Assert.Equal(3, SearchAlgorithms.ComputeDamerauLevenshtein("abc", "xyz"));
    }

    // ---------- DamerauLevenshteinDistance (API publique, avec cache) ----------

    [Fact]
    public void Api_publique_donne_le_meme_resultat_que_le_calcul_interne()
    {
        Assert.Equal(1, SearchAlgorithms.DamerauLevenshteinDistance("firfox", "firefox"));
    }

    [Fact]
    public void Api_publique_est_stable_au_second_appel_donc_le_cache_est_correct()
    {
        var first = SearchAlgorithms.DamerauLevenshteinDistance("calculatrice", "calculatrise");
        var second = SearchAlgorithms.DamerauLevenshteinDistance("calculatrice", "calculatrise");

        Assert.Equal(first, second);
    }

    // ---------- StripEmojis ----------

    [Fact]
    public void StripEmojis_retire_un_symbole_et_son_selecteur_de_variante()
    {
        // U+2699 (roue dentée) + U+FE0F (variation selector-16)
        Assert.Equal("Paramètres Windows", SearchAlgorithms.StripEmojis("⚙️ Paramètres Windows"));
    }

    [Fact]
    public void StripEmojis_retire_un_emoji_hors_BMP_en_paire_de_substitution()
    {
        // U+1F50A (haut-parleur) encodé en surrogate pair
        Assert.Equal("Volume", SearchAlgorithms.StripEmojis("🔊 Volume"));
    }

    [Fact]
    public void StripEmojis_laisse_intact_un_texte_sans_emoji()
    {
        Assert.Equal("Bloc-notes", SearchAlgorithms.StripEmojis("Bloc-notes"));
    }

    [Fact]
    public void StripEmojis_preserve_les_accents()
    {
        Assert.Equal("Éditeur de vidéo", SearchAlgorithms.StripEmojis("Éditeur de vidéo"));
    }

    [Fact]
    public void StripEmojis_coupe_les_espaces_de_bordure()
    {
        Assert.Equal("Réseau", SearchAlgorithms.StripEmojis("⚙️  Réseau  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void StripEmojis_gere_null_et_chaine_vide(string? input)
    {
        Assert.Equal(input, SearchAlgorithms.StripEmojis(input!));
    }

    [Fact]
    public void StripEmojis_retire_plusieurs_emojis()
    {
        Assert.Equal("Son et affichage", SearchAlgorithms.StripEmojis("🔊 Son et affichage ⚙️"));
    }

    /// <summary>
    /// Un libellé entièrement composé d'emojis doit se réduire à une chaîne vide
    /// plutôt que de laisser des demi-paires de substitution derrière lui.
    /// </summary>
    [Fact]
    public void StripEmojis_sur_un_libelle_100_pourcent_emoji_rend_une_chaine_vide()
    {
        Assert.Equal(string.Empty, SearchAlgorithms.StripEmojis("🔊⚙️"));
    }
}
