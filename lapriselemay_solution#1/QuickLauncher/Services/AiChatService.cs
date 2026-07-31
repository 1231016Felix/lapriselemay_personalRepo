using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using QuickLauncher.Services.AiProviders;

namespace QuickLauncher.Services;

/// <summary>
/// Service d'intégration IA pour la commande :ai.
/// Utilise un pattern strategy pour supporter plusieurs fournisseurs (OpenAI, Gemini, Ollama, etc.).
/// </summary>
public sealed class AiChatService : IDisposable
{
    private readonly HttpClient _httpClient;
    private bool _disposed;

    // Providers disponibles (indexés par ID)
    private static readonly Dictionary<string, IAiProvider> Providers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chatgpt"] = new OpenAiProvider(),
        ["ollama"]  = new OpenAiProvider(),   // Ollama est OpenAI-compatible
        ["custom"]  = new OpenAiProvider(),   // Custom utilise aussi le format OpenAI par défaut
        ["gemini"]  = new GeminiProvider(),
    };

    // Cache multi-entrées pour éviter les appels répétés identiques.
    // Capacité limitée à 10 entrées (les plus anciennes sont évincées).
    private readonly Dictionary<string, (AiChatResult Result, DateTime CachedAt)> _cache = new();
    private readonly object _cacheLock = new();
    private const int MaxCacheEntries = 10;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public AiChatService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "QuickLauncher/1.0");
    }

    /// <summary>
    /// Résout le provider à utiliser selon l'identifiant du fournisseur.
    /// </summary>
    public static IAiProvider ResolveProvider(string providerId)
    {
        return Providers.TryGetValue(providerId, out var provider)
            ? provider
            : Providers["chatgpt"]; // Fallback OpenAI-compatible
    }

    /// <summary>
    /// Envoie une question à l'API et retourne la réponse.
    /// </summary>
    public async Task<AiChatResult?> AskAsync(
        string question,
        string apiUrl,
        string apiKey,
        string model,
        string systemPrompt,
        string providerId,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return null;

        // Vérifier le cache (verrouillé : AskAsync peut être appelé en parallèle)
        var cacheKey = $"{question.ToLowerInvariant()}_{model}_{providerId}";
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) &&
                DateTime.Now - cached.CachedAt < CacheDuration)
            {
                return cached.Result;
            }
        }

        try
        {
            var provider = ResolveProvider(providerId);
            using var request = provider.BuildRequest(apiUrl, apiKey, model, systemPrompt, question);

            var response = await _httpClient.SendAsync(request, token);
            var responseJson = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[AI:{providerId}] Erreur HTTP {response.StatusCode}: {responseJson}");
                return new AiChatResult
                {
                    Error = ParseHttpError(response.StatusCode, responseJson, model, providerId)
                };
            }

            var parsed = provider.ParseResponse(responseJson);
            if (parsed.HasError)
            {
                return new AiChatResult { Error = parsed.Error };
            }

            var result = new AiChatResult
            {
                Question = question,
                Answer = parsed.Answer,
                Model = model,
                TokensUsed = parsed.TokensUsed
            };

            // Mettre en cache (évincer les entrées les plus anciennes si capacité atteinte)
            lock (_cacheLock)
            {
                if (_cache.Count >= MaxCacheEntries)
                {
                    var oldest = _cache.MinBy(kv => kv.Value.CachedAt).Key;
                    _cache.Remove(oldest);
                }
                _cache[cacheKey] = (result, DateTime.Now);
            }
            return result;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            Debug.WriteLine($"[AI:{providerId}] Connexion refusée: {ex.Message}");
            return new AiChatResult
            {
                Error = "Impossible de se connecter au serveur IA. Vérifiez que le service est lancé et que l'URL est correcte."
            };
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[AI:{providerId}] Erreur HTTP: {ex.Message}");
            return new AiChatResult { Error = "Erreur de connexion. Vérifiez votre réseau." };
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[AI:{providerId}] Erreur JSON: {ex.Message}");
            return new AiChatResult { Error = "Réponse invalide du serveur IA." };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AI:{providerId}] Erreur: {ex.Message}");
            return new AiChatResult { Error = $"Erreur: {ex.Message}" };
        }
    }

    /// <summary>
    /// Surcharge rétro-compatible (sans providerId → fallback OpenAI).
    /// </summary>
    public Task<AiChatResult?> AskAsync(
        string question, string apiUrl, string apiKey,
        string model, string systemPrompt,
        CancellationToken token = default)
        => AskAsync(question, apiUrl, apiKey, model, systemPrompt, "chatgpt", token);

    /// <summary>
    /// Teste la connectivité avec le serveur IA.
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(
        string apiUrl, string apiKey, string model, string providerId)
    {
        try
        {
            var result = await AskAsync("Dis simplement 'OK'.", apiUrl, apiKey, model,
                "Réponds uniquement 'OK'.", providerId, CancellationToken.None);

            if (result == null)
                return (false, "Aucune réponse");
            if (result.HasError)
                return (false, result.Error!);
            return (true, $"✅ Connecté — Modèle: {model}");
        }
        catch (Exception ex)
        {
            return (false, $"Erreur: {ex.Message}");
        }
    }

    /// <summary>
    /// Surcharge rétro-compatible (sans providerId).
    /// </summary>
    public Task<(bool Success, string Message)> TestConnectionAsync(
        string apiUrl, string apiKey, string model)
        => TestConnectionAsync(apiUrl, apiKey, model, "chatgpt");

    /// <summary>
    /// Parse les erreurs HTTP de manière adaptée au fournisseur.
    /// </summary>
    private static string ParseHttpError(
        System.Net.HttpStatusCode statusCode, string responseJson,
        string model, string providerId)
    {
        // Tenter d'extraire un message d'erreur du JSON
        try
        {
            var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("error", out var errorObj))
            {
                if (errorObj.TryGetProperty("message", out var msg))
                {
                    var errorMsg = msg.GetString();
                    if (!string.IsNullOrEmpty(errorMsg))
                        return errorMsg.Length > 200 ? errorMsg[..200] + "…" : errorMsg;
                }
            }
        }
        catch { /* Fallback vers les messages génériques */ }

        return statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized
                => "Clé API invalide ou manquante",
            System.Net.HttpStatusCode.Forbidden
                => "Accès refusé. Vérifiez votre clé API et les permissions.",
            System.Net.HttpStatusCode.NotFound
                => providerId == "gemini"
                    ? $"Modèle '{model}' introuvable. Essayez gemini-2.0-flash ou gemini-2.5-pro."
                    : $"Modèle '{model}' introuvable. Vérifiez le nom du modèle.",
            (System.Net.HttpStatusCode)429
                => providerId == "gemini"
                    ? "Quota Gemini atteint. Vérifiez votre quota sur console.cloud.google.com."
                    : "Limite atteinte ou crédits insuffisants. Vérifiez votre solde sur platform.openai.com/settings/organization/billing",
            _ => $"Erreur serveur ({(int)statusCode})"
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }
}

/// <summary>
/// Résultat d'une requête IA.
/// </summary>
public sealed class AiChatResult
{
    public string Question { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int? TokensUsed { get; init; }
    public string? Error { get; init; }

    public bool HasError => !string.IsNullOrEmpty(Error);
}
