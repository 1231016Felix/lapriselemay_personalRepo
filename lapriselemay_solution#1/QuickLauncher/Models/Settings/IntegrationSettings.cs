using System.Text.Json.Serialization;
using QuickLauncher.Services;

namespace QuickLauncher.Models.Settings;

/// <summary>
/// Paramètres des intégrations externes : météo, traduction, IA, notes, widgets.
/// </summary>
public sealed class IntegrationSettings
{
    // === Météo ===
    public string WeatherCity { get; set; } = "Montreal";
    public string WeatherUnit { get; set; } = "celsius";
    
    // === Traduction ===
    public string TranslateTargetLang { get; set; } = "en";
    public string TranslateSourceLang { get; set; } = "auto";
    
    // === IA ===
    public string AiProvider { get; set; } = "chatgpt";
    public string AiApiUrl { get; set; } = "https://api.openai.com/v1/chat/completions";
    
    /// <summary>
    /// Clé API chiffrée via DPAPI (préfixe "dpapi:" + Base64).
    /// Sérialisée dans le fichier settings.json — ne jamais lire directement.
    /// Utiliser <see cref="AiApiKeyDecrypted"/> pour obtenir la valeur en clair.
    /// </summary>
    public string AiApiKey { get; set; } = string.Empty;
    
    /// <summary>
    /// Clé API en clair (lecture/écriture).
    /// Le getter déchiffre la valeur stockée, le setter chiffre via DPAPI.
    /// Non sérialisé en JSON — seul <see cref="AiApiKey"/> est persisté.
    /// </summary>
    [JsonIgnore]
    public string AiApiKeyDecrypted
    {
        get => SecureStorageService.Decrypt(AiApiKey);
        set => AiApiKey = SecureStorageService.Encrypt(value);
    }
    
    public string AiModel { get; set; } = "gpt-4o-mini";
    public string AiSystemPrompt { get; set; } = "Tu es un assistant concis intégré dans un lanceur d'applications. Réponds de manière courte et directe (2-3 phrases max). Pas de markdown. Langue: français.";
    
    /// <summary>
    /// Délai en secondes après la dernière touche tapée avant d'envoyer la requête IA.
    /// Permet à l'utilisateur de finir sa question sans réponse prématurée.
    /// Plage : 1-4 secondes, défaut : 3.
    /// </summary>
    public int AiDebounceSeconds { get; set; } = Constants.AiDebounceSecondsDefault;
    
    // === Widgets de notes ===
    public List<NoteWidgetInfo> NoteWidgets { get; set; } = [];
    
    // === Widgets de minuteries ===
    public List<TimerWidgetInfo> TimerWidgets { get; set; } = [];
    
    // === Notes rapides ===
    public List<NoteItem> Notes { get; set; } = [];
    
    /// <summary>Copie profonde pour le pattern clone-swap du SettingsProvider.</summary>
    public IntegrationSettings Clone()
    {
        var clone = (IntegrationSettings)MemberwiseClone();
        // Copie PROFONDE des widgets : les services les modifient sur le clone (position,
        // pause) ; partager les objets reviendrait à muter un snapshot déjà publié.
        clone.NoteWidgets = NoteWidgets.Select(w => w.Clone()).ToList();
        clone.TimerWidgets = TimerWidgets.Select(w => w.Clone()).ToList();
        clone.Notes = [..Notes];
        return clone;
    }
}
