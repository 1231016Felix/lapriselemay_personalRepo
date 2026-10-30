using QuickLauncher.Models;
using QuickLauncher.Services.CommandHandlers;

namespace QuickLauncher.Services.CommandHandlers;

/// <summary>
/// Handler pour la commande :weather.
/// Interroge l'API Open-Meteo et retourne les résultats météo formatés.
/// </summary>
public sealed class WeatherCommandHandler : ICommandHandler
{
    private readonly WebIntegrationService _webService;
    private readonly ISettingsProvider _settingsProvider;

    public WeatherCommandHandler(WebIntegrationService webService, ISettingsProvider settingsProvider)
    {
        _webService = webService;
        _settingsProvider = settingsProvider;
    }

    public bool CanHandle(string query)
    {
        var settings = _settingsProvider.Current;
        var cmd = settings.SystemCommands.FirstOrDefault(c => c.Type == SystemControlType.Weather);
        if (cmd is not { IsEnabled: true }) return false;
        return query.StartsWith(cmd.FullPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CommandResult> ExecuteAsync(string query, CancellationToken token)
    {
        var settings = _settingsProvider.Current;
        var cmd = settings.SystemCommands.First(c => c.Type == SystemControlType.Weather);
        var weatherArg = query.Length > cmd.FullPrefix.Length + 1
            ? query[(cmd.FullPrefix.Length + 1)..].Trim()
            : null;

        var targetCity = string.IsNullOrWhiteSpace(weatherArg) ? settings.Integrations.WeatherCity : weatherArg;
        var result = await _webService.GetWeatherAsync(targetCity, settings.Integrations.WeatherUnit, token);

        if (token.IsCancellationRequested)
            return new CommandResult();

        var results = new List<SearchResult>();

        if (result is null or { HasError: true })
        {
            results.Add(new SearchResult
            {
                Name = $"❌ {result?.Error ?? "Impossible de récupérer la météo"}",
                Description = result?.HasError == true ? "Vérifiez le nom de la ville" : "Erreur réseau",
                Type = ResultType.SystemControl,
                DisplayIcon = "❌"
            });
        }
        else
        {
            var locationText = result.Country != null ? $"{result.City}, {result.Country}" : result.City ?? targetCity;
            results.Add(new SearchResult
            {
                Name = $"{result.WeatherIcon} {result.Temperature:F0}{result.UnitSymbol} — {result.WeatherDescription}",
                Description = $"📍 {locationText}",
                Type = ResultType.SystemControl, DisplayIcon = result.WeatherIcon,
                Path = ":weather:current", IsInfoBlock = true
            });
            results.Add(new SearchResult
            {
                Name = $"Ressenti {result.FeelsLike:F0}{result.UnitSymbol} • Humidité {result.Humidity}%",
                Description = $"💨 Vent {result.WindSpeed:F0} km/h {result.WindDirection}",
                Type = ResultType.SystemControl, DisplayIcon = "🌡️", IsInfoBlock = true
            });
            foreach (var forecast in result.Forecasts.Skip(1))
            {
                results.Add(new SearchResult
                {
                    Name = $"{forecast.Icon} {forecast.DayName}: {forecast.MaxTemp:F0}°/{forecast.MinTemp:F0}°",
                    Description = $"🌧️ Précipitations {forecast.PrecipitationProbability}%",
                    Type = ResultType.SystemControl, DisplayIcon = forecast.Icon, IsInfoBlock = true
                });
            }
        }

        return new CommandResult { Results = results };
    }
}
