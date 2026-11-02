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
        string sigPath = Path.Combine(stagingDir, "update.zip.sig");
        try
        {
            byte[] sig;
            using (var ms = new MemoryStream())
            {
                await GetAsync(http, sigUrl, ms, null, ct).ConfigureAwait(false);
                sig = ms.ToArray();
            }
            if (sig.Length == 0 || sig.Length > 1024)
                throw new UpdateException("Die Signatur fehlt. Bitte das Update von der Download-Seite holen.");
            await File.WriteAllBytesAsync(sigPath, sig, ct);
            long bytes;
            await using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await GetAsync(http, zipUrl, fs, progress, ct).ConfigureAwait(false);
                bytes = fs.Length;
            }
            Log.Info($"Update {info.Version}: downloaded {zipName}, {bytes} bytes");
            return PrepareStagedExe(stagingDir, publicKeyPem);
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

    /// <summary>
    /// Re-checks the staged ZIP signature and extracts the exe. Called after the download and
    /// again immediately before the installer starts, so a swapped file in %LOCALAPPDATA% is not run.
    /// </summary>
    public static string PrepareStagedExe(string stagingDir, string publicKeyPem = UpdateSignature.PublicKeyPem)
    {
        string zipPath = Path.Combine(stagingDir, "update.zip");
        string sigPath = Path.Combine(stagingDir, "update.zip.sig");
        string exePath = Path.Combine(stagingDir, UpdateInstallPath.ExeName);
        if (!File.Exists(zipPath) || !File.Exists(sigPath))
            throw new UpdateException("Die Datei ist beschädigt oder nicht von Magnus signiert. Nichts wurde installiert.");
        var sig = File.ReadAllBytes(sigPath);
        if (sig.Length == 0 || sig.Length > 1024)
            throw new UpdateException("Die Datei ist beschädigt oder nicht von Magnus signiert. Nichts wurde installiert.");
        bool ok;
        using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            ok = UpdateSignature.Verify(fs, sig, publicKeyPem);
        if (!ok)
        {
            Log.Error("Update: signature check failed for staged zip");
            ResetDir(stagingDir);
            throw new UpdateException("Die Datei ist beschädigt oder nicht von Magnus signiert. Nichts wurde installiert.");
        }
        ExtractSignedExe(zipPath, exePath);
        Log.Info("Update: extracted exe");
        return exePath;
    }

    private static void ExtractSignedExe(string zipPath, string exePath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, UpdateInstallPath.ExeName, StringComparison.OrdinalIgnoreCase));
        if (entry == null) throw new UpdateException("In der ZIP fehlt WhisperShortcut.exe.");
        var destDir = Path.GetDirectoryName(exePath)!;
        // entry.Name is the file name only, but FullName could be "folder/WhisperShortcut.exe" —
        // we always write to the fixed staging path, never to FullName.
        Directory.CreateDirectory(destDir);
        entry.ExtractToFile(exePath, overwrite: true);
    }

    private static void ResetDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }

    private static async Task GetAsync(HttpClient http, string url, Stream target, IProgress<double>? progress, CancellationToken ct)
    {
        if (!NetworkPolicy.IsAllowedDownloadUrl(url))
            throw new UpdateException("Download fehlgeschlagen. Bitte die Internetverbindung prüfen.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WhisperShortcut-Windows", null));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
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
