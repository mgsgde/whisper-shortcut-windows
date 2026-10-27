using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Serves bytes from 127.0.0.1, honouring Range unless told not to.</summary>
internal sealed class TestServer : IDisposable
{
    private readonly HttpListener _l = new();
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool IgnoreRange { get; set; }
    public int Requests { get; private set; }
    public string Url { get; }

    public TestServer()
    {
        var port = new Random().Next(20000, 60000);
        while (true)
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                l.Start();
                _l = l;
                break;
            }
            catch (HttpListenerException) { port++; }
        }
        Url = $"http://127.0.0.1:{port}/file.bin";
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _l.GetContextAsync(); } catch { return; }
            Requests++;
            var range = ctx.Request.Headers["Range"];
            long start = 0;
            if (!IgnoreRange && range != null && range.StartsWith("bytes="))
            {
                start = long.Parse(range["bytes=".Length..].TrimEnd('-'));
                ctx.Response.StatusCode = 206;
            }
            var len = Data.Length - start;
            ctx.Response.ContentLength64 = len;
            await ctx.Response.OutputStream.WriteAsync(Data.AsMemory((int)start));
            ctx.Response.Close();
        }
    }

    public void Dispose() => _l.Close();
}

public class ModelDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsw-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldRoot = Environment.GetEnvironmentVariable("WSW_OFFLINE_ROOT");

    public ModelDownloaderTests() => Environment.SetEnvironmentVariable("WSW_OFFLINE_ROOT", _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("WSW_OFFLINE_ROOT", _oldRoot);
        try { Directory.Delete(_root, true); } catch { }
    }

    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        new Random(7).NextBytes(b);
        return b;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static OfflineComponent Comp(string url, byte[] data, bool zip = false, string name = "m.bin") =>
        new("t", "Test", "testcomp", new[] { new ModelFile(name, url, data.Length, Sha(data)) }, zip);

    private sealed class Prog : IProgress<double>
    {
        public List<double> Values { get; } = new();
        public void Report(double v) => Values.Add(v);
    }

    [Fact]
    public async Task NotEnoughDiskSpace_FailsBeforeDownloading()
    {
        using var srv = new TestServer { Data = Bytes(10) };
        // One petabyte: no test machine has that much free.
        var c = new OfflineComponent("t", "Test", "testcomp",
            new[] { new ModelFile("m.bin", srv.Url, 1_000_000_000_000_000, Sha(Bytes(10))) }, false);
        var e = await Assert.ThrowsAsync<OfflineDownloadException>(() =>
            new ModelDownloader(new HttpClient()).DownloadAsync(c, null, CancellationToken.None));
        Assert.StartsWith("Nicht genug Speicherplatz: ", e.Message);
        Assert.EndsWith(" GB nötig.", e.Message);
        Assert.Equal(0, srv.Requests);
    }

    [Fact]
    public async Task FreshDownload_Installs_ProgressReachesOne()
    {
        var data = Bytes(500_000);
        using var srv = new TestServer { Data = data };
        var c = Comp(srv.Url, data);
        var prog = new Prog();
        Assert.False(ModelDownloader.IsInstalled(c));
        await new ModelDownloader(new HttpClient()).DownloadAsync(c, prog, CancellationToken.None);
        Assert.True(ModelDownloader.IsInstalled(c));
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(c.FullPath, "m.bin")));
        Assert.False(File.Exists(Path.Combine(c.FullPath, "m.bin.part")));
        Assert.Equal(1.0, prog.Values[^1]);
        Assert.Equal(prog.Values.OrderBy(v => v), prog.Values);
        Assert.Equal(data.Length, ModelDownloader.SizeOnDisk(c) - new FileInfo(Path.Combine(c.FullPath, ".complete")).Length);
        ModelDownloader.Delete(c);
        Assert.False(ModelDownloader.IsInstalled(c));
    }

    [Fact]
    public async Task ExistingPart_ResumesWithRange()
    {
        var data = Bytes(300_000);
        using var srv = new TestServer { Data = data };
        var c = Comp(srv.Url, data);
        Directory.CreateDirectory(c.FullPath);
        File.WriteAllBytes(Path.Combine(c.FullPath, "m.bin.part"), data[..100_000]);
        await new ModelDownloader(new HttpClient()).DownloadAsync(c, null, CancellationToken.None);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(c.FullPath, "m.bin")));
    }

    [Fact]
    public async Task ServerIgnoresRange_RestartsFromZero()
    {
        var data = Bytes(300_000);
        using var srv = new TestServer { Data = data, IgnoreRange = true };
        var c = Comp(srv.Url, data);
        Directory.CreateDirectory(c.FullPath);
        File.WriteAllBytes(Path.Combine(c.FullPath, "m.bin.part"), data[..100_000]);
        await new ModelDownloader(new HttpClient()).DownloadAsync(c, null, CancellationToken.None);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(c.FullPath, "m.bin")));
    }

    [Fact]
    public async Task WrongHash_DeletesPart_AndThrows()
    {
        var data = Bytes(100_000);
        using var srv = new TestServer { Data = data };
        var c = new OfflineComponent("t", "Test", "testcomp",
            new[] { new ModelFile("m.bin", srv.Url, data.Length, new string('0', 64)) });
        var ex = await Assert.ThrowsAsync<OfflineDownloadException>(() =>
            new ModelDownloader(new HttpClient()).DownloadAsync(c, null, CancellationToken.None));
        Assert.Equal("Download beschädigt, bitte noch einmal versuchen.", ex.Message);
        Assert.False(File.Exists(Path.Combine(c.FullPath, "m.bin.part")));
        Assert.False(ModelDownloader.IsInstalled(c));
    }

    [Fact]
    public async Task Unreachable_ThrowsNetworkMessage()
    {
        var data = Bytes(10);
        var c = Comp("http://127.0.0.1:1/x", data);
        var ex = await Assert.ThrowsAsync<OfflineDownloadException>(() =>
            new ModelDownloader(new HttpClient()).DownloadAsync(c, null, CancellationToken.None));
        Assert.Equal("Download fehlgeschlagen: keine Verbindung zum Server.", ex.Message);
    }

    [Fact]
    public async Task Zip_IsExtracted_ZipDeleted_MarkerWritten()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry("llama-server.exe").Open());
            w.Write("fake");
        }
        var data = ms.ToArray();
        using var srv = new TestServer { Data = data };
        var c = Comp(srv.Url, data, zip: true, name: "b.zip");
        await new ModelDownloader(new HttpClient()).DownloadAsync(c, null, CancellationToken.None);
        Assert.Equal("fake", File.ReadAllText(Path.Combine(c.FullPath, "llama-server.exe")));
        Assert.False(File.Exists(Path.Combine(c.FullPath, "b.zip")));
        Assert.True(ModelDownloader.IsInstalled(c));
    }

    [Fact]
    public async Task Cancel_KeepsPart()
    {
        var data = Bytes(200_000);
        using var srv = new TestServer { Data = data };
        var c = Comp(srv.Url, data);
        using var cts = new CancellationTokenSource();
        var prog = new CancelOnFirstBytes(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ModelDownloader(new HttpClient()).DownloadAsync(c, prog, cts.Token));
        Assert.True(File.Exists(Path.Combine(c.FullPath, "m.bin.part")));
        Assert.False(ModelDownloader.IsInstalled(c));
    }

    private sealed class CancelOnFirstBytes : IProgress<double>
    {
        private readonly CancellationTokenSource _cts;
        public CancelOnFirstBytes(CancellationTokenSource cts) => _cts = cts;
        public void Report(double v) { if (v > 0) _cts.Cancel(); }
    }
}
