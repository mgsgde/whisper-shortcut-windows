using System.Security.Cryptography;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class UpdateSignatureTests
{
    private static (string Pem, byte[] Sig) Sign(byte[] data)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportSubjectPublicKeyInfoPem(),
            key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void Verify_ValidSignature_IsTrue()
    {
        var data = "hallo zip"u8.ToArray();
        var (pem, sig) = Sign(data);
        Assert.True(UpdateSignature.Verify(new MemoryStream(data), sig, pem));
    }

    [Fact]
    public void Verify_FlippedByte_IsFalse()
    {
        var data = "hallo zip"u8.ToArray();
        var (pem, sig) = Sign(data);
        data[2] ^= 0xFF;
        Assert.False(UpdateSignature.Verify(new MemoryStream(data), sig, pem));
    }

    [Fact]
    public void Verify_GarbageSignature_IsFalse()
    {
        var (pem, _) = Sign(new byte[] { 1 });
        Assert.False(UpdateSignature.Verify(new MemoryStream(new byte[] { 1 }), new byte[] { 1, 2, 3 }, pem));
    }

    [Fact]
    public void PublicKeyPem_Imports()
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(UpdateSignature.PublicKeyPem);
    }
}
