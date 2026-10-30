using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace QuickLauncher.Services;

/// <summary>
/// Service d'intégration web pour les commandes :weather et :translate.
/// Utilise des APIs gratuites sans clé: Open-Meteo (météo) et MyMemory (traduction).
/// </summary>
public sealed class WebIntegrationService : IDisposable
{
    private readonly HttpClient _httpClient;
    private bool _disposed;
    
    // Cache simple pour éviter les appels répétés
    private (string Key, WeatherResult Result, DateTime CachedAt)? _weatherCache;
    private static readonly TimeSpan WeatherCacheDuration = TimeSpan.FromMinutes(10);

    public WebIntegrationService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "QuickLauncher/1.0");
    }

    #region Weather (Open-Meteo — gratuit, sans clé API)

    /// <summary>
    /// Valide un nom de ville via l'API de géocodage.
    /// Retourne le nom résolu et le pays si trouvé, null sinon.
    /// </summary>
    public async Task<(string City, string? Country)?> ValidateCityAsync(string city, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(city))
            return null;

        try
        {
            var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=fr";
            var geoJson = await _httpClient.GetStringAsync(geoUrl, token);
            var geoData = JsonDocument.Parse(geoJson);

            if (!geoData.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return null;

            var location = results[0];
            var resolvedCity = location.GetProperty("name").GetString() ?? city;
            var country = location.TryGetProperty("country", out var c) ? c.GetString() : null;
            return (resolvedCity, country);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Récupère la météo pour une ville donnée.
    /// </summary>
    public async Task<WeatherResult?> GetWeatherAsync(string city, string tempUnit = "celsius", CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(city))
            return null;

        var cacheKey = $"{city.ToLowerInvariant()}_{tempUnit}";
        
        // Vérifier le cache
        if (_weatherCache.HasValue && 
            _weatherCache.Value.Key == cacheKey &&
            DateTime.Now - _weatherCache.Value.CachedAt < WeatherCacheDuration)
        {
            return _weatherCache.Value.Result;
        }

        try
        {
            // Étape 1: Géocodage (nom de ville → coordonnées)
            var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=fr";
            var geoJson = await _httpClient.GetStringAsync(geoUrl, token);
            var geoData = JsonDocument.Parse(geoJson);
            
            if (!geoData.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return new WeatherResult { Error = $"Ville '{city}' introuvable" };

            var location = results[0];
            var lat = location.GetProperty("latitude").GetDouble();
            var lon = location.GetProperty("longitude").GetDouble();
            var resolvedCity = location.GetProperty("name").GetString() ?? city;
            var country = location.TryGetProperty("country", out var c) ? c.GetString() : null;

            // Étape 2: Météo actuelle
            var unit = tempUnit == "fahrenheit" ? "fahrenheit" : "celsius";
            var weatherUrl = $"https://api.open-meteo.com/v1/forecast?" +
                $"latitude={lat.ToString(CultureInfo.InvariantCulture)}" +
                $"&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                $"&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,wind_direction_10m" +
                $"&daily=temperature_2m_max,temperature_2m_min,weather_code,precipitation_probability_max" +
                $"&temperature_unit={unit}" +
                $"&wind_speed_unit=kmh" +
                $"&timezone=auto" +
                $"&forecast_days=3";

            var weatherJson = await _httpClient.GetStringAsync(weatherUrl, token);
            var weatherData = JsonDocument.Parse(weatherJson);
            var current = weatherData.RootElement.GetProperty("current");
            
            var weatherCode = current.GetProperty("weather_code").GetInt32();
            var temp = current.GetProperty("temperature_2m").GetDouble();
            var feelsLike = current.GetProperty("apparent_temperature").GetDouble();
            var humidity = current.GetProperty("relative_humidity_2m").GetInt32();
            var windSpeed = current.GetProperty("wind_speed_10m").GetDouble();
            var windDir = current.GetProperty("wind_direction_10m").GetInt32();
            var unitSymbol = unit == "fahrenheit" ? "°F" : "°C";

            // Prévisions 3 jours
            var daily = weatherData.RootElement.GetProperty("daily");
            var forecasts = new List<DayForecast>();
            var dates = daily.GetProperty("time");
            var maxTemps = daily.GetProperty("temperature_2m_max");
            var minTemps = daily.GetProperty("temperature_2m_min");
            var dayCodes = daily.GetProperty("weather_code");
            var precipProbs = daily.GetProperty("precipitation_probability_max");

            for (int i = 0; i < Math.Min(3, dates.GetArrayLength()); i++)
            {
                var date = DateTime.Parse(dates[i].GetString()!);
                forecasts.Add(new DayForecast
                {
                    Date = date,
                    DayName = i == 0 ? "Aujourd'hui" : date.ToString("ddd", new CultureInfo("fr-CA")),
                    MaxTemp = maxTemps[i].GetDouble(),
                    MinTemp = minTemps[i].GetDouble(),
                    WeatherCode = dayCodes[i].GetInt32(),
                    PrecipitationProbability = precipProbs[i].GetInt32(),
                    UnitSymbol = unitSymbol
                });
            }

            var result = new WeatherResult
            {
                City = resolvedCity,
                Country = country,
                Temperature = temp,
                FeelsLike = feelsLike,
                Humidity = humidity,
                WindSpeed = windSpeed,
                WindDirection = GetWindDirectionText(windDir),
                WeatherCode = weatherCode,
                WeatherDescription = GetWeatherDescription(weatherCode),
                WeatherIcon = GetWeatherIcon(weatherCode),
                UnitSymbol = unitSymbol,
                Forecasts = forecasts
            };

            // Mettre en cache
            _weatherCache = (cacheKey, result, DateTime.Now);
            return result;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[Weather] Erreur HTTP: {ex.Message}");
            return new WeatherResult { Error = "Erreur de connexion. Vérifiez votre réseau." };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Weather] Erreur: {ex.Message}");
            return new WeatherResult { Error = $"Erreur: {ex.Message}" };
        }
    }

    private static string GetWeatherIcon(int code) => code switch
    {
        0 => "☀️",
        1 or 2 => "⛅",
        3 => "☁️",
        45 or 48 => "🌫️",
        51 or 53 or 55 => "🌦️",
        56 or 57 => "🌧️❄️",
        61 or 63 => "🌧️",
        65 => "🌧️🌧️",
        66 or 67 => "🌧️❄️",
        71 or 73 => "🌨️",
        75 or 77 => "❄️",
        80 or 81 or 82 => "🌦️",
        85 or 86 => "🌨️",
        95 => "⛈️",
        96 or 99 => "⛈️🧊",
        _ => "🌡️"
    };

    private static string GetWeatherDescription(int code) => code switch
    {
        0 => "Ciel dégagé",
        1 => "Principalement dégagé",
        2 => "Partiellement nuageux",
        3 => "Couvert",
        45 => "Brouillard",
        48 => "Brouillard givrant",
        51 => "Bruine légère",
        53 => "Bruine modérée",
        55 => "Bruine dense",
        56 or 57 => "Bruine verglaçante",
        61 => "Pluie légère",
        63 => "Pluie modérée",
        65 => "Pluie forte",
        66 or 67 => "Pluie verglaçante",
        71 => "Neige légère",
        73 => "Neige modérée",
        75 => "Neige forte",
        77 => "Grains de neige",
        80 => "Averses légères",
        81 => "Averses modérées",
        82 => "Averses violentes",
        85 => "Averses de neige légères",
        86 => "Averses de neige fortes",
        95 => "Orage",
        96 or 99 => "Orage avec grêle",
        _ => "Inconnu"
    };

    private static string GetWindDirectionText(int degrees) => degrees switch
    {
        >= 337 or < 23 => "N",
        >= 23 and < 67 => "NE",
        >= 67 and < 112 => "E",
        >= 112 and < 157 => "SE",
        >= 157 and < 202 => "S",
        >= 202 and < 247 => "SO",
        >= 247 and < 292 => "O",
        >= 292 and < 337 => "NO"
    };

    #endregion

    #region Translation (MyMemory — gratuit, sans clé API, 5000 chars/jour)

    /// <summary>
    /// Traduit un texte. Source auto-détectée, cible configurable.
    /// </summary>
    public async Task<TranslationResult?> TranslateAsync(string text, string targetLang = "en", string sourceLang = "auto", CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            // MyMemory utilise le format "source|target" pour le paramètre langpair.
            // En mode auto, « Autodetect » fait détecter la langue par le service : l'ancienne
            // paire supposée (fr|en ou en|fr) traduisait de l'espagnol comme du français.
            var isAuto = string.Equals(sourceLang, "auto", StringComparison.OrdinalIgnoreCase);
            var effectiveSource = isAuto ? "Autodetect" : sourceLang;
            var langPair = $"{effectiveSource}|{targetLang}";
            
            var url = $"https://api.mymemory.translated.net/get?" +
                $"q={Uri.EscapeDataString(text)}" +
                $"&langpair={Uri.EscapeDataString(langPair)}";

            var json = await _httpClient.GetStringAsync(url, token);
            var data = JsonDocument.Parse(json);
            var responseData = data.RootElement.GetProperty("responseData");
            
            var translatedText = responseData.GetProperty("translatedText").GetString();
            var matchQuality = responseData.TryGetProperty("match", out var match) 
                ? match.GetDouble() : 0;

            // Langue source détectée (mode auto) : champ detectedLanguage de la réponse,
            // à défaut la source de la correspondance exacte dans « matches »
            var detectedLang = isAuto ? ReadDetectedLanguage(responseData) ?? "auto" : sourceLang;
            if (isAuto && detectedLang == "auto" && data.RootElement.TryGetProperty("matches", out var matches)
                && matches.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in matches.EnumerateArray())
                {
                    if (m.TryGetProperty("segment", out var seg) && 
                        seg.GetString()?.Equals(text, StringComparison.OrdinalIgnoreCase) == true &&
                        m.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String)
                    {
                        detectedLang = src.GetString() ?? "auto";
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(translatedText) || 
                translatedText.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
            {
                return new TranslationResult { Error = "Limite de traduction atteinte. Réessayez plus tard." };
            }

            return new TranslationResult
            {
                OriginalText = text,
                TranslatedText = translatedText,
                SourceLanguage = detectedLang,
                TargetLanguage = targetLang,
                SourceLanguageName = GetLanguageName(detectedLang),
                TargetLanguageName = GetLanguageName(targetLang),
                Quality = matchQuality
            };
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[Translate] Erreur HTTP: {ex.Message}");
            return new TranslationResult { Error = "Erreur de connexion. Vérifiez votre réseau." };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Translate] Erreur: {ex.Message}");
            return new TranslationResult { Error = $"Erreur: {ex.Message}" };
        }
    }

    /// <summary>
    /// Lit la langue détectée par MyMemory (« es », « es-ES » ou objet { language }) et la
    /// ramène à un code de deux lettres ; null si absente.
    /// </summary>
    internal static string? ReadDetectedLanguage(JsonElement responseData)
    {
        if (!responseData.TryGetProperty("detectedLanguage", out var detected))
            return null;

        var raw = detected.ValueKind switch
        {
            JsonValueKind.String => detected.GetString(),
            JsonValueKind.Object when detected.TryGetProperty("language", out var lang) => lang.GetString(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(raw)) return null;
        var code = raw.Split('-', '_')[0].Trim().ToLowerInvariant();
        return code.Length is >= 2 and <= 3 ? code : null;
    }

    /// <summary>
    /// Langues supportées avec leurs codes ISO.
    /// </summary>
    public static readonly Dictionary<string, string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fr"] = "Français",
        ["en"] = "Anglais",
        ["es"] = "Espagnol",
        ["de"] = "Allemand",
        ["it"] = "Italien",
        ["pt"] = "Portugais",
        ["nl"] = "Néerlandais",
        ["ru"] = "Russe",
        ["zh"] = "Chinois",
        ["ja"] = "Japonais",
        ["ko"] = "Coréen",
        ["ar"] = "Arabe",
        ["hi"] = "Hindi",
        ["pl"] = "Polonais",
        ["uk"] = "Ukrainien",
        ["sv"] = "Suédois",
        ["da"] = "Danois",
        ["fi"] = "Finnois",
        ["no"] = "Norvégien",
        ["tr"] = "Turc",
        ["el"] = "Grec",
        ["he"] = "Hébreu",
        ["th"] = "Thaï",
        ["vi"] = "Vietnamien",
        ["cs"] = "Tchèque",
        ["ro"] = "Roumain",
        ["hu"] = "Hongrois"
    };

    /// <summary>
    /// Alias courants vers les codes ISO 2 lettres.
    /// </summary>
    public static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en", ["english"] = "en", ["anglais"] = "en",
        ["fra"] = "fr", ["fre"] = "fr", ["french"] = "fr", ["francais"] = "fr", ["français"] = "fr",
        ["spa"] = "es", ["spanish"] = "es", ["espagnol"] = "es",
        ["deu"] = "de", ["ger"] = "de", ["german"] = "de", ["allemand"] = "de",
        ["ita"] = "it", ["italian"] = "it", ["italien"] = "it",
        ["por"] = "pt", ["portuguese"] = "pt", ["portugais"] = "pt",
        ["nld"] = "nl", ["dut"] = "nl", ["dutch"] = "nl", ["néerlandais"] = "nl",
        ["rus"] = "ru", ["russian"] = "ru", ["russe"] = "ru",
        ["zho"] = "zh", ["chi"] = "zh", ["chinese"] = "zh", ["chinois"] = "zh",
        ["jpn"] = "ja", ["japanese"] = "ja", ["japonais"] = "ja",
        ["kor"] = "ko", ["korean"] = "ko", ["coréen"] = "ko",
        ["ara"] = "ar", ["arabic"] = "ar", ["arabe"] = "ar",
        ["pol"] = "pl", ["polish"] = "pl", ["polonais"] = "pl",
        ["tur"] = "tr", ["turkish"] = "tr", ["turc"] = "tr",
        ["swe"] = "sv", ["swedish"] = "sv", ["suédois"] = "sv",
    };

    /// <summary>
    /// Résout un code ou alias de langue vers le code ISO 2 lettres.
    /// </summary>
    public static string? ResolveLanguageCode(string input)
    {
        if (SupportedLanguages.ContainsKey(input)) return input;
        return LanguageAliases.TryGetValue(input, out var code) ? code : null;
    }

    private static string GetLanguageName(string code)
    {
        if (string.IsNullOrEmpty(code) || code == "auto")
            return "Auto";
        
        // Gérer les codes composés (ex: "fr-FR" → "fr")
        var shortCode = code.Contains('-') ? code.Split('-')[0] : code;
        return SupportedLanguages.TryGetValue(shortCode, out var name) ? name : code.ToUpperInvariant();
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }

}

#region Result Models

/// <summary>
/// Résultat de la requête météo.
/// </summary>
public sealed class WeatherResult
{
    public string? City { get; init; }
    public string? Country { get; init; }
    public double Temperature { get; init; }
    public double FeelsLike { get; init; }
    public int Humidity { get; init; }
    public double WindSpeed { get; init; }
    public string WindDirection { get; init; } = string.Empty;
    public int WeatherCode { get; init; }
    public string WeatherDescription { get; init; } = string.Empty;
    public string WeatherIcon { get; init; } = "🌡️";
    public string UnitSymbol { get; init; } = "°C";
    public List<DayForecast> Forecasts { get; init; } = [];
    public string? Error { get; init; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>
    /// Ligne principale: "☀️ 22°C — Ciel dégagé"
    /// </summary>
    public string Summary => HasError ? Error! : $"{WeatherIcon} {Temperature:F0}{UnitSymbol} — {WeatherDescription}";

    /// <summary>
    /// Détails: "Ressenti 20°C • Humidité 45% • Vent 12 km/h NO"
    /// </summary>
    public string Details => HasError ? string.Empty :
        $"Ressenti {FeelsLike:F0}{UnitSymbol} • Humidité {Humidity}% • Vent {WindSpeed:F0} km/h {WindDirection}";

    /// <summary>
    /// Ligne de prévisions compacte: "Demain: ⛅ 18°/12° • Ven: 🌧️ 15°/10°"
    /// </summary>
    public string ForecastSummary
    {
        get
        {
            if (HasError || Forecasts.Count <= 1) return string.Empty;
            return string.Join(" • ", Forecasts.Skip(1).Select(f => 
                $"{f.DayName}: {f.Icon} {f.MaxTemp:F0}°/{f.MinTemp:F0}° 🌧{f.PrecipitationProbability}%"));
        }
    }
}

/// <summary>
/// Prévision pour un jour.
/// </summary>
public sealed class DayForecast
{
    public DateTime Date { get; init; }
    public string DayName { get; init; } = string.Empty;
    public double MaxTemp { get; init; }
    public double MinTemp { get; init; }
    public int WeatherCode { get; init; }
    public int PrecipitationProbability { get; init; }
    public string UnitSymbol { get; init; } = "°C";

    public string Icon => WeatherCode switch
    {
        0 => "☀️", 1 or 2 => "⛅", 3 => "☁️",
        45 or 48 => "🌫️", >= 51 and <= 57 => "🌦️",
        >= 61 and <= 67 => "🌧️", >= 71 and <= 77 => "❄️",
        >= 80 and <= 82 => "🌦️", >= 85 and <= 86 => "🌨️",
        >= 95 => "⛈️", _ => "🌡️"
    };
}

/// <summary>
/// Résultat de traduction.
/// </summary>
public sealed class TranslationResult
{
    public string OriginalText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
    public string SourceLanguage { get; init; } = string.Empty;
    public string TargetLanguage { get; init; } = string.Empty;
    public string SourceLanguageName { get; init; } = string.Empty;
    public string TargetLanguageName { get; init; } = string.Empty;
    public double Quality { get; init; }
    public string? Error { get; init; }

    public bool HasError => !string.IsNullOrEmpty(Error);
}

#endregion
