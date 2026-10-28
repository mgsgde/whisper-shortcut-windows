using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;

namespace WhisperShortcut.Core;

/// <summary>User-facing (German) failure of the download or the signature check.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>Downloads the release ZIP for this CPU, verifies its signature and extracts the new exe.</summary>
public static class UpdateDownloader
{
    /// <summary>
    /// Returns the path of the extracted exe in <paramref name="stagingDir"/>. Throws <see cref="UpdateException"/>
    /// (nothing installed) or <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, Architecture arch,
        string stagingDir, IProgress<double>? progress, CancellationToken ct,
        string publicKeyPem = UpdateSignature.PublicKeyPem)
    {
        string zipName = UpdateCheck.AssetName(arch);
        if (!info.Assets.TryGetValue(zipName, out var zipUrl))
            throw new UpdateException($"Für diesen PC gibt es in Version {info.Version} keine Datei.");
        if (!info.Assets.TryGetValue(zipName + ".sig", out var sigUrl))
            throw new UpdateException("Die Signatur fehlt. Bitte das Update von der Download-Seite holen.");

        ResetDir(stagingDir);
        string zipPath = Path.Combine(stagingDir, "update.zip");
        string exePath = Path.Combine(stagingDir, "WhisperShortcut.exe");
        try
        {
            byte[] sig;
            using (var ms = new MemoryStream())
            {
                await GetAsync(http, sigUrl, ms, null, ct);
                sig = ms.ToArray();
            }
            long bytes;
            await using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await GetAsync(http, zipUrl, fs, progress, ct);
                bytes = fs.Length;
            }
            Log.Info($"Update {info.Version}: downloaded {zipName}, {bytes} bytes");

            bool ok;
            await using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                ok = UpdateSignature.Verify(fs, sig, publicKeyPem);
            if (!ok)
            {
                Log.Error($"Update {info.Version}: signature check failed for {zipName}");
                ResetDir(stagingDir);
                throw new UpdateException("Die Datei ist beschädigt oder nicht von Magnus signiert. Nichts wurde installiert.");
            }

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, "WhisperShortcut.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new UpdateException("In der ZIP fehlt WhisperShortcut.exe.");
                entry.ExtractToFile(exePath, overwrite: true);
            }
            File.Delete(zipPath);
            Log.Info($"Update {info.Version}: extracted exe");
            return exePath;
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw new UpdateException("Die Datei ist beschädigt oder nicht von Magnus signiert. Nichts wurde installiert.");
        }
    }

    private static void ResetDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }

    private static async Task GetAsync(HttpClient http, string url, Stream target, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WhisperShortcut-Windows", null));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total is > 0) progress?.Report(Math.Min(1.0, (double)done / total.Value));
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Error("Update download failed", ex);
            throw new UpdateException("Download fehlgeschlagen. Bitte die Internetverbindung prüfen.");
        }
    }
}
