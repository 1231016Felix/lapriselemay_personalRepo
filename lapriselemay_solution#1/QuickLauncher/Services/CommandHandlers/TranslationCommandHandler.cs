using QuickLauncher.Models;

namespace QuickLauncher.Services.CommandHandlers;

/// <summary>
/// Handler pour la commande :translate.
/// Interroge l'API MyMemory et retourne le résultat de traduction formaté.
/// </summary>
public sealed class TranslationCommandHandler : ICommandHandler
{
    private readonly WebIntegrationService _webService;
    private readonly ISettingsProvider _settingsProvider;

    public TranslationCommandHandler(WebIntegrationService webService, ISettingsProvider settingsProvider)
    {
        _webService = webService;
        _settingsProvider = settingsProvider;
    }

    public bool CanHandle(string query)
    {
        var settings = _settingsProvider.Current;
        var cmd = settings.SystemCommands.FirstOrDefault(c => c.Type == SystemControlType.Translate);
        if (cmd is not { IsEnabled: true }) return false;
        return query.StartsWith($":{cmd.Prefix} ", StringComparison.OrdinalIgnoreCase) && query.Length > cmd.Prefix.Length + 2;
    }

    public async Task<CommandResult> ExecuteAsync(string query, CancellationToken token)
    {
        var settings = _settingsProvider.Current;
        var cmd = settings.SystemCommands.First(c => c.Type == SystemControlType.Translate);
        var translateArg = query[(cmd.Prefix.Length + 2)..].Trim();

        if (string.IsNullOrWhiteSpace(translateArg))
            return new CommandResult
            {
                Results = [new SearchResult
                {
                    Name = "🌐 Entrez du texte à traduire",
                    Description = "Formats: :translate hello • :translate fr hello • :translate fr>en bonjour",
                    Type = ResultType.SystemControl, DisplayIcon = "🌐"
                }]
            };

        var (sourceLang, targetLang, textToTranslate) = ParseTranslationInput(translateArg, settings);

        if (string.IsNullOrWhiteSpace(textToTranslate))
            return new CommandResult
            {
                Results = [new SearchResult
                {
                    Name = "🌐 Entrez du texte à traduire",
                    Description = "Formats: :translate hello • :translate fr hello • :translate fr>en bonjour",
                    Type = ResultType.SystemControl, DisplayIcon = "🌐"
                }]
            };

        var result = await _webService.TranslateAsync(textToTranslate, targetLang, sourceLang, token);

        if (token.IsCancellationRequested)
            return new CommandResult();

        var results = new List<SearchResult>();

        if (result is null or { HasError: true })
        {
            results.Add(new SearchResult
            {
                Name = $"❌ {result?.Error ?? "Impossible d'obtenir la traduction"}",
                Description = result?.HasError == true ? "Réessayez plus tard" : "Erreur réseau",
                Type = ResultType.SystemControl, DisplayIcon = "❌"
            });
        }
        else
        {
            results.Add(new SearchResult
            {
                Name = result.TranslatedText,
                Description = $"{result.SourceLanguageName} → {result.TargetLanguageName} • Entrée pour copier",
                Type = ResultType.SystemControl, DisplayIcon = "🌐",
                Path = $":translate:copy:{result.TranslatedText}", IsInfoBlock = true
            });
            results.Add(new SearchResult
            {
                Name = $"📝 {result.OriginalText}",
                Description = $"Texte original ({result.SourceLanguageName})",
                Type = ResultType.SystemControl, DisplayIcon = "📝", IsInfoBlock = true
            });
        }

        return new CommandResult { Results = results };
    }

    /// <summary>
    /// Parse l'input de traduction pour en extraire les langues et le texte.
    /// </summary>
    internal static (string Source, string Target, string Text) ParseTranslationInput(string input, AppSettings settings)
    {
        var defaultTarget = settings.Integrations.TranslateTargetLang;
        var defaultSource = settings.Integrations.TranslateSourceLang;
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 1)
            return (defaultSource, defaultTarget, string.Empty);

        var firstWord = parts[0].ToLowerInvariant().Trim('[', ']');

        // Format "fr>en texte"
        if (firstWord.Contains('>'))
        {
            var langs = firstWord.Split('>', 2);
            var resolvedSrc = WebIntegrationService.ResolveLanguageCode(langs[0]);
            var resolvedDst = langs.Length == 2 ? WebIntegrationService.ResolveLanguageCode(langs[1]) : null;
            if (resolvedSrc != null && resolvedDst != null)
                return (resolvedSrc, resolvedDst, parts.Length > 1 ? parts[1] : string.Empty);
        }

        // Format "fr texte"
        var resolvedLang = WebIntegrationService.ResolveLanguageCode(firstWord);
        if (parts.Length >= 2 && resolvedLang != null)
            return (defaultSource, resolvedLang, parts[1]);

        return (defaultSource, defaultTarget, input);
    }
}
