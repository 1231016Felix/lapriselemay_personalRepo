using System.IO;
using QuickLauncher.Services;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Règles d'éligibilité partagées par le scan complet et le FileWatcher.
/// </summary>
public class IndexRulesTests
{
    private static readonly string[] Roots = [@"C:\Users\x\Documents", @"C:\Users\x\Documents\Projets"];
    private static readonly string[] Extensions = [".txt", ".xlsx", ".lnk"];

    private static FileAttributes? NoneHidden(string _) => FileAttributes.Normal;

    [Theory]
    [InlineData(@"C:\Data\a.txt", @"C:\Data", true)]
    [InlineData(@"C:\Database\a.txt", @"C:\Data", false)]
    [InlineData(@"C:\Data", @"C:\Data", false)]
    [InlineData(@"c:\data\sub\a.txt", @"C:\Data\", true)]
    public void IsUnder_respecte_les_frontieres_de_segment(string path, string folder, bool expected)
        => Assert.Equal(expected, IndexRules.IsUnder(path, folder));

    [Fact]
    public void FindRoot_choisit_la_racine_la_plus_profonde()
    {
        Assert.Equal(@"C:\Users\x\Documents\Projets", IndexRules.FindRoot(@"C:\Users\x\Documents\Projets\a\b.txt", Roots));
        Assert.Equal(@"C:\Users\x\Documents", IndexRules.FindRoot(@"C:\Users\x\Documents\Notes\b.txt", Roots));
        Assert.Null(IndexRules.FindRoot(@"C:\Autre\b.txt", Roots));
    }

    [Theory]
    [InlineData(@"C:\R", @"C:\R", 0)]
    [InlineData(@"C:\R", @"C:\R\a", 1)]
    [InlineData(@"C:\R\", @"C:\R\a\b\c", 3)]
    public void DirectoryDepth_suit_la_convention_du_scan(string root, string directory, int expected)
        => Assert.Equal(expected, IndexRules.DirectoryDepth(root, directory));

    [Theory]
    [InlineData(@"C:\R\~$rapport.docx", true)]
    [InlineData(@"C:\R\data.tmp", true)]
    [InlineData(@"C:\R\data.temp", true)]
    [InlineData(@"C:\R\rapport.txt", false)]
    public void Fichiers_temporaires(string path, bool expected)
        => Assert.Equal(expected, IndexRules.IsTemporaryFile(path));

    [Fact]
    public void Fichier_eligible_si_extension_profondeur_et_visibilite_conviennent()
    {
        Assert.True(IndexRules.IsEligibleFile(@"C:\Users\x\Documents\a\b.txt", Roots, Extensions, 2, false, NoneHidden));
        Assert.False(IndexRules.IsEligibleFile(@"C:\Users\x\Documents\a\b.pdf", Roots, Extensions, 2, false, NoneHidden));
        Assert.False(IndexRules.IsEligibleFile(@"C:\Users\x\Documents\a\b\c\d.txt", Roots, Extensions, 2, false, NoneHidden));
        Assert.False(IndexRules.IsEligibleFile(@"C:\Ailleurs\b.txt", Roots, Extensions, 2, false, NoneHidden));
    }

    [Fact]
    public void Un_dossier_ancetre_cache_exclut_le_fichier_sauf_option()
    {
        FileAttributes? HiddenGit(string path) =>
            path.EndsWith(@"\.git", StringComparison.OrdinalIgnoreCase) ? FileAttributes.Hidden : FileAttributes.Normal;

        var file = @"C:\Users\x\Documents\repo\.git\notes.txt";
        Assert.False(IndexRules.IsEligibleFile(file, Roots, Extensions, 5, includeHidden: false, HiddenGit));
        Assert.True(IndexRules.IsEligibleFile(file, Roots, Extensions, 5, includeHidden: true, HiddenGit));
    }

    [Fact]
    public void Un_dossier_avec_un_point_dans_son_nom_reste_un_dossier_eligible()
        => Assert.True(IndexRules.IsEligibleDirectory(@"C:\Users\x\Documents\Project.v2", Roots, 3, false, NoneHidden));
}
