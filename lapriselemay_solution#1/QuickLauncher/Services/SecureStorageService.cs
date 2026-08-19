using System.Security.Cryptography;
using System.Text;

namespace QuickLauncher.Services;

/// <summary>
/// Service de stockage sécurisé utilisant la DPAPI (Data Protection API) de Windows.
/// Chiffre les données sensibles (clés API, tokens) avec la clé du profil utilisateur Windows.
/// Les données chiffrées ne sont lisibles que par le même utilisateur sur la même machine.
/// 
/// Format de stockage : "dpapi:" + Base64(données chiffrées).
/// Le préfixe permet de distinguer les valeurs chiffrées des valeurs legacy en clair
/// pour la migration transparente.
/// </summary>
public static class SecureStorageService
{
    /// <summary>
    /// Entropie additionnelle spécifique à l'application.
    /// Empêche une autre application utilisant DPAPI de déchiffrer nos données
    /// même si elle tourne sous le même utilisateur Windows.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuickLauncher.SecureStorage.v1");
    
    /// <summary>
    /// Préfixe identifiant une valeur chiffrée par ce service.
    /// </summary>
    private const string EncryptedPrefix = "dpapi:";

    /// <summary>
    /// Chiffre une chaîne en clair avec DPAPI (scope CurrentUser).
    /// </summary>
    /// <param name="plainText">Texte en clair à protéger.</param>
    /// <returns>Chaîne préfixée "dpapi:" + Base64, ou <see cref="string.Empty"/> si l'entrée est vide.</returns>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        try
        {
            var data = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
            return EncryptedPrefix + Convert.ToBase64String(encrypted);
        }
        catch (CryptographicException ex)
        {
            // En cas d'échec DPAPI (très rare), retourner la valeur en clair
            // plutôt que perdre la clé de l'utilisateur. Ce repli doit être
            // visible en production : la valeur sera persistée NON chiffrée.
            Log.Warning($"[SecureStorage] Échec du chiffrement DPAPI — la valeur sera stockée en clair: {ex.Message}");
            return plainText;
        }
    }

    /// <summary>
    /// Déchiffre une valeur protégée par <see cref="Encrypt"/>.
    /// Si la valeur n'est pas préfixée "dpapi:", elle est retournée telle quelle
    /// (rétro-compatibilité avec les clés stockées en clair avant la migration).
    /// </summary>
    /// <param name="storedValue">Valeur stockée (chiffrée ou legacy en clair).</param>
    /// <returns>Texte en clair, ou <see cref="string.Empty"/> si la valeur est vide ou corrompue.</returns>
    public static string Decrypt(string storedValue)
    {
        if (string.IsNullOrEmpty(storedValue))
            return string.Empty;

        // Valeur legacy non chiffrée → retourner telle quelle
        if (!storedValue.StartsWith(EncryptedPrefix))
            return storedValue;

        try
        {
            var base64 = storedValue[EncryptedPrefix.Length..];
            var encrypted = Convert.FromBase64String(base64);
            var decrypted = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException ex)
        {
            Log.Warning($"[SecureStorage] Échec du déchiffrement — la clé stockée est perdue ou vient d'un autre profil/machine: {ex.Message}");
            return string.Empty;
        }
        catch (FormatException ex)
        {
            Log.Warning($"[SecureStorage] Valeur stockée corrompue (Base64 invalide): {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Indique si une valeur est déjà chiffrée par ce service.
    /// Utilisé pour la migration automatique des clés legacy en clair.
    /// </summary>
    public static bool IsEncrypted(string value)
    {
        return !string.IsNullOrEmpty(value) && value.StartsWith(EncryptedPrefix);
    }
}
