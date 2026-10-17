using System.Security.Cryptography;
using System.Text;

namespace WhisperShortcut;

/// <summary>Encrypts the API key with DPAPI for the current Windows user (CurrentUser scope).</summary>
internal sealed class DpapiProtector : Core.ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WhisperShortcut.Windows.ApiKey");

    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public string? Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Settings copied from another user or PC: the key can't be decrypted, ask for it again.
            Core.Log.Error("API key could not be decrypted, treating as empty");
            return "";
        }
    }
}
