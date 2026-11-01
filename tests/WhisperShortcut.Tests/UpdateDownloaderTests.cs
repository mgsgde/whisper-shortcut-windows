using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class UpdateDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wsw-upd-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class BytesHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!files.TryGetValue(request.RequestUri!.ToString(), out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private static byte[] MakeZip(string entryName, byte[] content)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry(entryName).Open();
            s.Write(content);
        }
        return ms.ToArray();
    }

    private (HttpClient Http, UpdateInfo Info) Setup(byte[] zip, byte[]? sig, bool withSigAsset = true)
    {
        string name = UpdateCheck.AssetName(Architecture.X64);
        var files = new Dictionary<string, byte[]> { ["https://x/zip"] = zip };
        var assets = new Dictionary<string, string> { [name] = "https://x/zip" };
        if (withSigAsset)
        {
            files["https://x/sig"] = sig ?? _key.SignData(zip, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            assets[name + ".sig"] = "https://x/sig";
        }
        return (new HttpClient(new BytesHandler(files)), new UpdateInfo("9.9.9", "https://x") { Assets = assets });
    }

    private Task<string> Run(HttpClient http, UpdateInfo info, IProgress<double>? progress = null) =>
        UpdateDownloader.DownloadAsync(http, info, Architecture.X64, Path.Combine(_dir, "stage"), progress,
            CancellationToken.None, _key.ExportSubjectPublicKeyInfoPem());

    [Fact]
    public async Task ValidZip_ReturnsExe()
    {
        var exe = new byte[] { 1, 2, 3, 4, 5 };
        var (http, info) = Setup(MakeZip("WhisperShortcut/WhisperShortcut.exe", exe), null);
        double last = 0;
        var path = await Run(http, info, new Progress<double>(p => last = p));
        Assert.Equal(exe, File.ReadAllBytes(path));
        Assert.True(File.Exists(Path.Combine(_dir, "stage", "update.zip")));
        Assert.True(File.Exists(Path.Combine(_dir, "stage", "update.zip.sig")));
    }

    [Fact]
    public async Task HttpAssetUrl_Throws()
    {
        string name = UpdateCheck.AssetName(Architecture.X64);
        var info = new UpdateInfo("9.9.9", "https://x")
        {
            Assets = new Dictionary<string, string>
            {
                [name] = "http://evil.example/zip",
                [name + ".sig"] = "http://evil.example/sig",
            },
        };
        await Assert.ThrowsAsync<UpdateException>(() => Run(new HttpClient(new BytesHandler(new())), info));
    }

    [Fact]
    public async Task PrepareStagedExe_ReExtractsFromVerifiedZip()
    {
        var exe = new byte[] { 9, 8, 7 };
        var (http, info) = Setup(MakeZip("WhisperShortcut.exe", exe), null);
        var stage = Path.Combine(_dir, "stage");
        var path = await Run(http, info);
        File.WriteAllBytes(path, new byte[] { 1, 1, 1 });
        var again = UpdateDownloader.PrepareStagedExe(stage, _key.ExportSubjectPublicKeyInfoPem());
        Assert.Equal(exe, File.ReadAllBytes(again));
    }

    [Fact]
    public async Task BadSignature_Throws_AndLeavesNoExe()
    {
        var zip = MakeZip("WhisperShortcut/WhisperShortcut.exe", new byte[] { 1 });
        var (http, info) = Setup(zip, new byte[] { 9, 9, 9 });
        await Assert.ThrowsAsync<UpdateException>(() => Run(http, info));
        Assert.False(File.Exists(Path.Combine(_dir, "stage", "WhisperShortcut.exe")));
    }

    [Fact]
    public async Task MissingSigAsset_Throws()
    {
        var (http, info) = Setup(MakeZip("WhisperShortcut/WhisperShortcut.exe", new byte[] { 1 }), null, withSigAsset: false);
        await Assert.ThrowsAsync<UpdateException>(() => Run(http, info));
    }

    [Fact]
    public async Task ZipWithoutExe_Throws()
    {
        var (http, info) = Setup(MakeZip("WhisperShortcut/ANLEITUNG.txt", new byte[] { 1 }), null);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Run(http, info));
        Assert.Contains("WhisperShortcut.exe", ex.Message);
    }

    [Fact]
    public async Task MissingZipAsset_Throws()
    {
        var info = new UpdateInfo("9.9.9", "https://x");
        await Assert.ThrowsAsync<UpdateException>(() => Run(new HttpClient(new BytesHandler(new())), info));
    }
}
