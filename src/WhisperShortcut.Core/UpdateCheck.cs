using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WhisperShortcut.Core;

/// <summary>The newest published release: version without leading "v", and its web page.</summary>
public sealed record UpdateInfo(string Version, string Url);

/// <summary>Asks GitHub for the latest release and compares versions. Nothing is downloaded or installed.</summary>
public static class UpdateCheck
{
    /// <summary>GitHub API: latest published (non-draft, non-prerelease) release.</summary>
    public const string LatestReleaseApi = "https://api.github.com/repos/mgsgde/whisper-shortcut-windows/releases/latest";

    /// <summary>Page to send the user to.</summary>
    public const string ReleasesPage = "https://github.com/mgsgde/whisper-shortcut-windows/releases/latest";

    /// <summary>Reads <c>tag_name</c> (leading v/V stripped) and <c>html_url</c>; null on bad JSON or missing tag.</summary>
    public static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) return null;
            var version = tag.GetString()!.Trim().TrimStart('v', 'V');
            if (version.Length == 0) return null;
            string url = ReleasesPage;
            if (doc.RootElement.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(u.GetString()))
                url = u.GetString()!;
            return new UpdateInfo(version, url);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="latest"/> is a higher dotted numeric version than <paramref name="current"/>.
    /// A "+build" suffix is ignored; a "-prerelease" suffix is ignored too (1.2.0-beta counts as 1.2.0).
    /// Unparseable input yields false.
    /// </summary>
    public static bool IsNewer(string latest, string current)
    {
        var a = Numbers(latest);
        var b = Numbers(current);
        if (a == null || b == null) return false;
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    private static int[]? Numbers(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        int cut = v.IndexOfAny(new[] { '+', '-' });
        if (cut >= 0) v = v[..cut];
        if (v.Length == 0) return null;
        var parts = v.Split('.');
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, null, out result[i])) return null;
        return result;
    }

    /// <summary>
    /// Fetches the latest release. Null when no release exists (404). Throws <see cref="HttpRequestException"/>
    /// on network failure or any other bad status, so the caller can say "keine Verbindung".
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WhisperShortcut-Windows", null));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
