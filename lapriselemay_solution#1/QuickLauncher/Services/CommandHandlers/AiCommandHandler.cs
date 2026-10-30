using QuickLauncher.Models;

namespace QuickLauncher.Services.CommandHandlers;

/// <summary>
/// Handler pour la commande :ai.
/// Envoie la question à l'API IA configurée et retourne la réponse formatée.
/// </summary>
public sealed class AiCommandHandler : ICommandHandler
{
    private readonly AiChatService _aiService;
    private readonly ISettingsProvider _settingsProvider;

    public AiCommandHandler(AiChatService aiService, ISettingsProvider settingsProvider)
    {
        _aiService = aiService;
        _settingsProvider = settingsProvider;
    }

    public bool CanHandle(string query)
    {
        var settings = _settingsProvider.Current;
        var cmd = settings.SystemCommands.FirstOrDefault(c => c.Type == SystemControlType.AiChat);
        if (cmd is not { IsEnabled: true }) return false;
        return query.StartsWith($":{cmd.Prefix} ", StringComparison.OrdinalIgnoreCase) && query.Length > cmd.Prefix.Length + 2;
    }

    public async Task<CommandResult> ExecuteAsync(string query, CancellationToken token)
    {
        var settings = _settingsProvider.Current;
        var cmd = settings.SystemCommands.First(c => c.Type == SystemControlType.AiChat);
        var question = query[(cmd.Prefix.Length + 2)..].Trim();

        if (question.Length < 2)
            return new CommandResult();

        var result = await _aiService.AskAsync(
            question, settings.Integrations.AiApiUrl, settings.Integrations.AiApiKeyDecrypted,
            settings.Integrations.AiModel, settings.Integrations.AiSystemPrompt, settings.Integrations.AiProvider, token);

        if (token.IsCancellationRequested)
            return new CommandResult();

        var results = new List<SearchResult>();

        if (result is null or { HasError: true })
        {
            results.Add(new SearchResult
            {
                Name = $"❌ {result?.Error ?? "L'assistant IA n'a pas répondu"}",
                Description = result?.HasError == true
                    ? "Vérifiez la configuration dans Paramètres → Intégrations web" : "Aucune réponse",
                Type = ResultType.SystemControl, DisplayIcon = "❌"
            });
        }
        else
        {
            var answerLines = result.Answer
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            if (answerLines.Count == 0)
                answerLines.Add(result.Answer.Trim());

            results.Add(new SearchResult
            {
                Name = answerLines[0],
                Description = $"🤖 {result.Model} • Entrée pour copier",
                Type = ResultType.SystemControl, DisplayIcon = "🤖",
                Path = $":ai:copy:{result.Answer}", IsInfoBlock = true
            });

            foreach (var line in answerLines.Skip(1))
            {
                results.Add(new SearchResult
                {
                    Name = line, Description = string.Empty,
                    Type = ResultType.SystemControl, DisplayIcon = "   ",
                    Path = $":ai:copy:{result.Answer}", IsInfoBlock = true
                });
            }

            if (result.TokensUsed.HasValue)
            {
                results.Add(new SearchResult
                {
                    Name = $"📊 {result.TokensUsed} tokens utilisés",
                    Description = $"Question: {question}",
                    Type = ResultType.SystemControl, DisplayIcon = "📊", IsInfoBlock = true
                });
            }
        }

        return new CommandResult { Results = results };
    }
}
