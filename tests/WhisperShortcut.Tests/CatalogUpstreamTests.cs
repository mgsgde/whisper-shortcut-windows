using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Skipped unless env WSW_CATALOG_ONLINE=1: the check needs the network.</summary>
public sealed class UpstreamFactAttribute : FactAttribute
{
    public UpstreamFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WSW_CATALOG_ONLINE") != "1")
            Skip = "set WSW_CATALOG_ONLINE=1 to compare the offline catalog with the upstream files";
    }
}

/// <summary>
/// Compares every pinned file of the offline catalog with what the host serves today. Hugging Face
/// replaced files upstream once, so the pinned hashes no longer matched and the download failed.
/// </summary>
[Trait("Category", "Upstream")]
public class CatalogUpstreamTests
{
    private const long MaxDownload = 64L * 1024 * 1024;

    [UpstreamFact]
    public async Task EveryCatalogFile_StillMatchesUpstream()
    {
        var components = new List<OfflineComponent>
        {
            OfflineCatalog.Parakeet,
            OfflineCatalog.SileroVad,
            OfflineCatalog.LlamaServer(Architecture.X64),
            OfflineCatalog.LlamaServer(Architecture.Arm64),
        };
        components.AddRange(OfflineCatalog.Llms.Select(l => l.Component));

        using var head = NewClient(allowRedirect: false);
        using var full = NewClient(allowRedirect: true);
        var mismatches = new List<string>();
        foreach (var file in components.SelectMany(c => c.Files))
            mismatches.AddRange(await Check(file, head, full));

        Assert.True(mismatches.Count == 0, "Catalog differs from upstream:\n" + string.Join("\n", mismatches));
    }

    private static HttpClient NewClient(bool allowRedirect)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = allowRedirect })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WhisperShortcut-catalog-check");
        return client;
    }

    private static async Task<List<string>> Check(ModelFile file, HttpClient head, HttpClient full)
    {
        var bad = new List<string>();
        string Line(string what, object expected, object got) =>
            $"{file.RelativePath} ({file.Url}): {what} expected {expected}, got {got}";

        try
        {
            using var response = await head.SendAsync(new HttpRequestMessage(HttpMethod.Head, file.Url));
            IEnumerable<string>? sizes = null, etags = null;
            var hasLinked = response.Headers.TryGetValues("X-Linked-Size", out sizes)
                & response.Headers.TryGetValues("X-Linked-ETag", out etags);
            if (hasLinked)
            {
                var size = sizes!.First();
                var etag = etags!.First().Trim();
                if (etag.StartsWith("W/")) etag = etag[2..];
                etag = etag.Trim('"');
                if (size != file.Size.ToString()) bad.Add(Line("size", file.Size, size));
                // Only LFS files carry the SHA-256; plain git files carry a 40-char blob id, which says nothing about the content.
                if (etag.Length == 64)
                {
                    if (!string.Equals(etag, file.Sha256, StringComparison.OrdinalIgnoreCase)) bad.Add(Line("sha256", file.Sha256, etag));
                    return bad;
                }
                if (bad.Count > 0) return bad;
            }

            // Redirects (e.g. GitHub releases) are fine here; only an error status means the file is gone.
            if ((int)response.StatusCode >= 400)
            {
                bad.Add(Line("status", "success", (int)response.StatusCode + " " + response.StatusCode));
                return bad;
            }
        }
        catch (HttpRequestException ex)
        {
            bad.Add(Line("request", "success", ex.Message));
            return bad;
        }

        if (file.Size > MaxDownload)
        {
            bad.Add(Line("verification", "X-Linked headers or a small file", "cannot verify: no X-Linked headers and too large to download"));
            return bad;
        }

        try
        {
            using var response = await full.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                bad.Add(Line("status", "success", (int)response.StatusCode + " " + response.StatusCode));
                return bad;
            }
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var sha = SHA256.Create();
            long length = 0;
            var buffer = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0) { sha.TransformBlock(buffer, 0, n, null, 0); length += n; }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (length != file.Size) bad.Add(Line("size", file.Size, length));
            if (hex != file.Sha256.ToLowerInvariant()) bad.Add(Line("sha256", file.Sha256, hex));
        }
        catch (HttpRequestException ex)
        {
            bad.Add(Line("download", "success", ex.Message));
        }
        return bad;
    }
}
