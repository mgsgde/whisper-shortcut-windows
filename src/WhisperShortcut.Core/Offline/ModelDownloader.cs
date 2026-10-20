using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace WhisperShortcut.Core;

/// <summary>
/// Downloads the files of an <see cref="OfflineComponent"/> with resume and SHA-256 verification.
/// A component counts as installed once its ".complete" marker exists, which is written last, so a
/// half-finished download is never mistaken for a usable model.
/// </summary>
public sealed class ModelDownloader
{
    private const string Marker = ".complete";
    private const int BufferSize = 80 * 1024;

    private readonly HttpClient _http;

    /// <summary>
    /// Creates the downloader. Use an HttpClient with a long (or infinite) <c>Timeout</c>: the
    /// per-request timeout would otherwise cut off multi-GB bodies; cancel through the token instead.
    /// </summary>
    public ModelDownloader(HttpClient http) => _http = http;

    /// <summary>True when the component was downloaded and verified completely.</summary>
    public static bool IsInstalled(OfflineComponent c) => File.Exists(Path.Combine(c.FullPath, Marker));

    /// <summary>Bytes the component's folder occupies (0 when absent).</summary>
    public static long SizeOnDisk(OfflineComponent c)
    {
        var dir = c.FullPath;
        if (!Directory.Exists(dir)) return 0;
        return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    }

    /// <summary>Removes the component's folder with everything in it.</summary>
    public static void Delete(OfflineComponent c)
    {
        if (Directory.Exists(c.FullPath)) Directory.Delete(c.FullPath, recursive: true);
    }

    /// <summary>
    /// Downloads and verifies all files. Progress runs 0..1 over the bytes of the whole component.
    /// Cancellation keeps the .part file so the next call resumes.
    /// </summary>
    public async Task DownloadAsync(OfflineComponent c, IProgress<double>? progress, CancellationToken ct)
    {
        if (IsInstalled(c)) { progress?.Report(1.0); return; }
        var dir = c.FullPath;
        Directory.CreateDirectory(dir);
        var total = c.Files.Sum(f => f.Size);
        long done = 0;
        Log.Info($"offline download start: {c.Id}, {total} bytes");
        progress?.Report(0);

        foreach (var file in c.Files)
        {
            var target = Path.Combine(dir, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && new FileInfo(target).Length == file.Size)
            {
                done += file.Size;
                progress?.Report(Fraction(done, total));
                continue;
            }
            var before = done;
            await DownloadFileAsync(file, target, n => progress?.Report(Fraction(before + n, total)), ct).ConfigureAwait(false);
            done += file.Size;
        }

        if (c.ExtractZip)
        {
            foreach (var file in c.Files)
            {
                var zip = Path.Combine(dir, file.RelativePath);
                try
                {
                    using (var archive = ZipFile.OpenRead(zip)) { _ = archive.Entries.Count; }
                    ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
                }
                catch (InvalidDataException ex)
                {
                    File.Delete(zip);
                    throw new OfflineDownloadException("Download beschädigt, bitte noch einmal versuchen.", ex);
                }
                File.Delete(zip);
            }
        }

        File.WriteAllText(Path.Combine(dir, Marker), DateTime.UtcNow.ToString("O"));
        progress?.Report(1.0);
        Log.Info($"offline download done: {c.Id}, {SizeOnDisk(c)} bytes on disk");
    }

    private static double Fraction(long done, long total) => total <= 0 ? 1.0 : Math.Min(1.0, (double)done / total);

    private async Task DownloadFileAsync(ModelFile file, string target, Action<long> bytes, CancellationToken ct)
    {
        var part = target + ".part";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long have = 0;

        if (File.Exists(part))
        {
            have = new FileInfo(part).Length;
            if (have > file.Size) { File.Delete(part); have = 0; }
        }
        if (have > 0) have = await HashExistingAsync(part, hash, ct).ConfigureAwait(false);

        if (have < file.Size)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, file.Url);
                if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (have > 0 && resp.StatusCode == HttpStatusCode.OK)
                {
                    // Server ignored the Range header: start over, the hashed prefix is void.
                    hash.GetHashAndReset();
                    have = 0;
                }
                else if (!resp.IsSuccessStatusCode)
                {
                    throw new OfflineDownloadException($"Download fehlgeschlagen: Server antwortet mit Status {(int)resp.StatusCode}.");
                }

                await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);
                var buf = new byte[BufferSize];
                while (true)
                {
                    int n;
                    try { n = await net.ReadAsync(buf, ct).ConfigureAwait(false); }
                    catch (IOException ex) { throw Network(ex); }
                    if (n == 0) break;
                    hash.AppendData(buf, 0, n);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    have += n;
                    bytes(have);
                }
            }
            catch (HttpRequestException ex) { throw Network(ex); }
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (have != file.Size || !string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Log.Error($"offline download mismatch: {file.RelativePath} size {have}/{file.Size}");
            File.Delete(part);
            throw new OfflineDownloadException("Download beschädigt, bitte noch einmal versuchen.");
        }
        File.Move(part, target, overwrite: true);
    }

    private static OfflineDownloadException Network(Exception inner) =>
        new("Download fehlgeschlagen: keine Verbindung zum Server.", inner);

    private static async Task<long> HashExistingAsync(string path, IncrementalHash hash, CancellationToken ct)
    {
        long n = 0;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var buf = new byte[BufferSize];
        int r;
        while ((r = await fs.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buf, 0, r);
            n += r;
        }
        return n;
    }
}
