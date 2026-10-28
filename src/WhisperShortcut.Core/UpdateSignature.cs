using System.Security.Cryptography;

namespace WhisperShortcut.Core;

/// <summary>Checks the detached ECDSA P-256 signature (DER, over SHA-256 of the whole ZIP) of a release.</summary>
public static class UpdateSignature
{
    // The private key lives at ~/.config/whispershortcut-windows-update/ecdsa-p256-private.pem on Magnus's Mac.
    // Losing it means installed copies can never auto-update again.
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIGkFAXRFBxK/idCNsp2vSb0J/3Ox
        Nbtj6qi/x+t9kM15VnTo1SwgN6jnCT6Z0x7Q7VDPnbCaF8G9E+eBKpdU3A==
        -----END PUBLIC KEY-----
        """;

    /// <summary>True when <paramref name="signature"/> is valid for <paramref name="data"/>; any crypto error is false.</summary>
    public static bool Verify(Stream data, byte[] signature, string publicKeyPem = PublicKeyPem)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
