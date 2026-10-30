using QuickLauncher.Models;

namespace QuickLauncher.Services.CommandHandlers;

/// <summary>
/// Handler pour la commande :find.
/// Recherche dans tout le système via Windows Search / Everything / recherche manuelle.
/// </summary>
public sealed class WindowsSearchCommandHandler : ICommandHandler
{
    private readonly ISettingsProvider _settingsProvider;
    private readonly UniversalSearchService _universalSearchService;

    public WindowsSearchCommandHandler(ISettingsProvider settingsProvider, UniversalSearchService universalSearchService)
    {
        _settingsProvider = settingsProvider;
        _universalSearchService = universalSearchService;
    }

    public bool CanHandle(string query)
    {
        var settings = _settingsProvider.Current;
        var cmd = settings.SystemCommands.FirstOrDefault(c => c.Type == SystemControlType.SystemSearch);
        if (cmd is not { IsEnabled: true }) return false;
        var prefix = cmd.Prefix;
        return query.StartsWith($":{prefix} ", StringComparison.OrdinalIgnoreCase) && query.Length > prefix.Length + 2;
    }

    public async Task<CommandResult> ExecuteAsync(string query, CancellationToken token)
    {
        var settings = _settingsProvider.Current;
        var cmd = settings.SystemCommands.First(c => c.Type == SystemControlType.SystemSearch);
        var searchQuery = query[(cmd.Prefix.Length + 2)..].Trim();

        if (searchQuery.Length < 2)
            return new CommandResult();

        _universalSearchService.MaxSearchDepth = settings.Search.SystemSearchDepth;

        var searchResults = await _universalSearchService.SearchAsync(searchQuery, null, token);

        if (token.IsCancellationRequested)
            return new CommandResult();

        var results = new List<SearchResult>();

        if (searchResults.Count == 0)
        {
            results.Add(new SearchResult
            {
                Name = "Aucun résultat",
                Description = $"Aucun fichier trouvé pour '{searchQuery}'",
                Type = ResultType.SystemCommand, DisplayIcon = "❌"
            });
        }
        else
        {
            results.AddRange(searchResults);
        }

        return new CommandResult { Results = results };
    }
}
