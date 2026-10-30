using System.Text.Json;
using QuickLauncher.Models;
using QuickLauncher.Services;
using QuickLauncher.Services.CommandHandlers;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Exclusions de la recherche directe, clé du cache IA, détection de langue et casse
/// des arguments de traduction.
/// </summary>
public class SearchRulesTests
{
    [Theory]
    [InlineData(@"C:\Users\Robin\Documents", false)]
    [InlineData(@"C:\Projects\ObjectModel", false)]
    [InlineData(@"C:\Users\x\Documents\Objectifs 2026", false)]
    [InlineData(@"C:\Projects\App\bin", true)]
    [InlineData(@"C:\Projects\App\obj\Debug", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\abc", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Microsoft", false)]
    [InlineData(@"C:\src\node_modules\lib", true)]
    public void Exclusions_par_segment_complet(string directory, bool expected)
        => Assert.Equal(expected, UniversalSearchService.IsExcludedDirectory(directory));

    [Fact]
    public void Cle_de_cache_IA_depend_de_toute_la_configuration()
    {
        var reference = AiChatService.BuildCacheKey("Qu'est-ce qu'une API ?", "https://a/v1", "cle", "m1", "prompt", "chatgpt");

        Assert.Equal(reference, AiChatService.BuildCacheKey("Qu'est-ce qu'une API ?", "https://a/v1", "cle", "m1", "prompt", "chatgpt"));
        Assert.NotEqual(reference, AiChatService.BuildCacheKey("Qu'est-ce qu'une API ?", "https://b/v1", "cle", "m1", "prompt", "chatgpt"));
        Assert.NotEqual(reference, AiChatService.BuildCacheKey("Qu'est-ce qu'une API ?", "https://a/v1", "cle", "m1", "autre consigne", "chatgpt"));
        Assert.NotEqual(reference, AiChatService.BuildCacheKey("Qu'est-ce qu'une API ?", "https://a/v1", "cle", "m2", "prompt", "chatgpt"));
        Assert.NotEqual(reference, AiChatService.BuildCacheKey("Qu'est-ce qu'une API ?", "https://a/v1", "autre-cle", "m1", "prompt", "chatgpt"));
        Assert.NotEqual(reference, AiChatService.BuildCacheKey("qu'est-ce qu'une api ?", "https://a/v1", "cle", "m1", "prompt", "chatgpt"));
    }

    [Fact]
    public void Cle_de_cache_IA_ne_contient_pas_la_cle_API()
        => Assert.DoesNotContain("sk-SECRET-123", AiChatService.BuildCacheKey("q", "u", "sk-SECRET-123", "m", "p", "chatgpt"));

    [Theory]
    [InlineData("""{"detectedLanguage":"es"}""", "es")]
    [InlineData("""{"detectedLanguage":"es-ES"}""", "es")]
    [InlineData("""{"detectedLanguage":{"language":"de"}}""", "de")]
    [InlineData("""{"translatedText":"x"}""", null)]
    public void Langue_detectee_par_MyMemory(string json, string? expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, WebIntegrationService.ReadDetectedLanguage(doc.RootElement));
    }

    [Fact]
    public void Le_texte_a_traduire_garde_sa_casse()
    {
        var (source, target, text) = TranslationCommandHandler.ParseTranslationInput("FR>EN Bonjour NASA et iPhone", new AppSettings());
        Assert.Equal("fr", source);
        Assert.Equal("en", target);
        Assert.Equal("Bonjour NASA et iPhone", text);
    }
}
