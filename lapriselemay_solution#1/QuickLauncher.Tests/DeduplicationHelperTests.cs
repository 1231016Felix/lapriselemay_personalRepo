using QuickLauncher.Models;
using QuickLauncher.Services;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Identité des résultats : les fichiers homonymes ne sont plus fusionnés ; seules les
/// entrées d'application (raccourci, AppsFolder, paramètres) le sont par nom.
/// </summary>
public class DeduplicationHelperTests
{
    private static SearchResult Item(string name, string path, ResultType type)
        => new() { Name = name, Path = path, Type = type };

    private static string Key(SearchResult item) => DeduplicationHelper.GetIdentityKey(item);

    [Fact]
    public void Deux_fichiers_homonymes_de_dossiers_differents_restent_distincts()
    {
        var a = Item("budget", @"C:\Clients\Alpha\budget.xlsx", ResultType.File);
        var b = Item("budget", @"C:\Clients\Beta\budget.xlsx", ResultType.File);
        Assert.NotEqual(Key(a), Key(b));
    }

    [Fact]
    public void Meme_nom_extensions_differentes_restent_distincts()
    {
        var docx = Item("rapport", @"C:\Docs\rapport.docx", ResultType.File);
        var pdf = Item("rapport", @"C:\Docs\rapport.pdf", ResultType.File);
        Assert.NotEqual(Key(docx), Key(pdf));
    }

    [Fact]
    public void Meme_fichier_avec_une_casse_differente_est_fusionne()
    {
        var a = Item("budget", @"C:\Docs\Budget.xlsx", ResultType.File);
        var b = Item("budget", @"c:\docs\budget.xlsx", ResultType.File);
        Assert.Equal(Key(a), Key(b));
    }

    [Fact]
    public void Raccourci_et_app_AppsFolder_de_meme_nom_sont_fusionnes()
    {
        var shortcut = Item("Calculatrice", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Calculatrice.lnk", ResultType.Application);
        var storeApp = Item("Calculatrice", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", ResultType.StoreApp);
        Assert.Equal(Key(shortcut), Key(storeApp));
    }

    [Fact]
    public void Executable_d_un_dossier_utilisateur_garde_son_chemin_comme_identite()
    {
        var a = Item("setup", @"C:\Users\x\Downloads\A\setup.exe", ResultType.Application);
        var b = Item("setup", @"C:\Users\x\Downloads\B\setup.exe", ResultType.Application);
        Assert.NotEqual(Key(a), Key(b));
    }
}
