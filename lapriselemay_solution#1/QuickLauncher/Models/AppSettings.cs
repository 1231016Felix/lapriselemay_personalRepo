using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickLauncher.Models.Settings;

namespace QuickLauncher.Models;

/// <summary>
/// Agrégateur de paramètres. Regroupe les sections spécialisées et gère
/// la sérialisation / migration du format JSON.
/// 
/// Types support (PinnedItem, HistoryItem, ScoringWeights, etc.) sont dans leurs propres fichiers.
/// Commandes par défaut : voir <see cref="DefaultSystemCommands"/>.
/// Migration legacy : voir <see cref="LegacySettingsMigrator"/>.
/// </summary>
public sealed class AppSettings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Constants.AppName);
    
    private static readonly string SettingsPath = Path.Combine(SettingsDir, Constants.SettingsFileName);
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    
    // ══════════════════════════════════════════════════════════
    //  SECTIONS
    // ══════════════════════════════════════════════════════════
    
    /// <summary>Section recherche, indexation, scoring, historique.</summary>
    public SearchSettings Search { get; set; } = new();
    
    /// <summary>Section apparence, thème, animations, fenêtre.</summary>
    public AppearanceSettings Appearance { get; set; } = new();
    
    /// <summary>Section intégrations : météo, traduction, IA, widgets.</summary>
    public IntegrationSettings Integrations { get; set; } = new();
    
    // ══════════════════════════════════════════════════════════
    //  PROPRIÉTÉS RACINE
    // ══════════════════════════════════════════════════════════
    
    /// <summary>Commandes de contrôle système (:volume, :lock, etc.).</summary>
    public List<SystemControlCommand> SystemCommands { get; set; } = DefaultSystemCommands.Create();
    
    /// <summary>Raccourci clavier global.</summary>
    public HotkeySettings Hotkey { get; set; } = new();
    
    /// <summary>Comportement général.</summary>
    public bool StartWithWindows { get; set; } = true;
    public bool CloseAfterLaunch { get; set; } = true;
    public bool MinimizeOnStartup { get; set; } = true;
    public bool SingleClickLaunch { get; set; }
    
    public void ResetSystemCommands() => SystemCommands = DefaultSystemCommands.Create();
    
    /// <summary>
    /// Copie profonde de tous les paramètres.
    /// Utilisée par SettingsProvider pour le pattern clone-swap :
    /// les threads de recherche travaillent sur un snapshot stable
    /// pendant que le UI thread mute une nouvelle copie.
    /// </summary>
    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.Search = Search.Clone();
        clone.Appearance = Appearance.Clone();
        clone.Integrations = Integrations.Clone();
        clone.SystemCommands = SystemCommands.Select(c => c.Clone()).ToList();
        clone.Hotkey = new HotkeySettings
        {
            UseAlt = Hotkey.UseAlt, UseCtrl = Hotkey.UseCtrl,
            UseShift = Hotkey.UseShift, UseWin = Hotkey.UseWin, Key = Hotkey.Key
        };
        return clone;
    }
    
    // ══════════════════════════════════════════════════════════
    //  CHARGEMENT / SAUVEGARDE
    // ══════════════════════════════════════════════════════════
    
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            
            // Détection du format : ancien (plat) vs nouveau (sections)
            using var doc = JsonDocument.Parse(json);
            var isNewFormat = doc.RootElement.TryGetProperty("search", out _);
            
            AppSettings? settings;
            
            if (isNewFormat)
            {
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            }
            else
            {
                // Ancien format plat → migration déléguée à LegacySettingsMigrator
                settings = LegacySettingsMigrator.Migrate(json, JsonOptions);
            }
            
            if (settings != null)
            {
                DefaultSystemCommands.Migrate(settings.SystemCommands);
                
                // Migration : chiffrer la clé API si elle est encore en clair (legacy)
                if (!string.IsNullOrEmpty(settings.Integrations.AiApiKey) &&
                    !Services.SecureStorageService.IsEncrypted(settings.Integrations.AiApiKey))
                {
                    var plainKey = settings.Integrations.AiApiKey;
                    settings.Integrations.AiApiKeyDecrypted = plainKey;
                    settings.Save();
                    Services.Log.Info("[Settings] Clé API migrée vers DPAPI");
                }

                Services.Log.Debug($"[Settings] Chargé avec {settings.Search.PinnedItems.Count} épingles (format: {(isNewFormat ? "v2" : "legacy→v2")})");
                return settings;
            }
        }
        catch (Exception ex)
        {
            // Retour silencieux aux valeurs par défaut = perte de tous les réglages
            // pour l'utilisateur : cet échec doit être visible en production.
            Services.Log.Error("[Settings] Échec du chargement — retour aux paramètres par défaut", ex);
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);

            // Écriture atomique : écrire d'abord dans un fichier temporaire, puis remplacer
            // la cible en une seule opération de renommage. Sur le même volume (NTFS), File.Move
            // avec overwrite s'appuie sur MoveFileEx/REPLACE_EXISTING, qui est atomique :
            // settings.json contient toujours soit l'ancienne version complète, soit la nouvelle,
            // jamais un fichier tronqué. Évite la perte totale des réglages (épingles, historique,
            // clé API) si l'application est interrompue en plein milieu de l'écriture.
            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsPath, overwrite: true);

            Services.Log.Debug($"[Settings] Sauvegardé avec {Search.PinnedItems.Count} épingles");
        }
        catch (Exception ex)
        {
            // Les modifications (épingles, historique, clé API) ne sont PAS persistées.
            Services.Log.Error("[Settings] Échec de la sauvegarde", ex);
        }
    }
    
    public static void Reset()
    {
        try { if (File.Exists(SettingsPath)) File.Delete(SettingsPath); }
        catch { /* Ignore */ }
    }
    
    public static string GetSettingsPath() => SettingsPath;
}