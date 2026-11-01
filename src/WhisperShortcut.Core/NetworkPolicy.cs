using System.Net;

namespace WhisperShortcut.Core;

/// <summary>
/// What the app may fetch or open: HTTPS everywhere, plus loopback HTTP for the local LLM and
/// the VM test feed. Credential headers never ride a redirect to another host.
/// </summary>
public static class NetworkPolicy
{
    /// <summary>HTTPS, or HTTP on 127.0.0.1 / localhost / ::1.</summary>
    public static bool IsAllowedDownloadUri(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    /// <summary>True when <paramref name="url"/> is an absolute allowed download URI.</summary>
    public static bool IsAllowedDownloadUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowedDownloadUri(uri);

    /// <summary>Only https://… — what we pass to the browser (release page, docs, issue form).</summary>
    public static bool IsAllowedBrowserUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host);

    /// <summary>True when <paramref name="url"/> is an absolute HTTPS URI.</summary>
    public static bool IsAllowedBrowserUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowedBrowserUri(uri);

    /// <summary>HttpClient that never follows redirects (Gemini, OpenAI, update-feed JSON, local LLM).</summary>
    public static HttpClient CreateApiClient(TimeSpan? timeout = null) =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = timeout ?? Timeout.InfiniteTimeSpan,
        };

    /// <summary>
    /// HttpClient for ZIP / model downloads: follows GET redirects only to another allowed URI,
    /// and drops credential headers when the host changes.
    /// </summary>
    public static HttpClient CreateDownloadClient(TimeSpan timeout) =>
        new(new SafeRedirectHandler(new HttpClientHandler { AllowAutoRedirect = false }))
        {
            Timeout = timeout,
        };
}

/// <summary>
/// Follows a short chain of GET/HEAD redirects. HTTPS→HTTP is refused (also refused by .NET,
/// but we never send the body or <c>x-goog-api-key</c> to another host either).
/// </summary>
public sealed class SafeRedirectHandler : DelegatingHandler
{
    /// <summary>Same order of magnitude as the default <see cref="HttpClientHandler.MaxAutomaticRedirections"/>.</summary>
    public const int MaxRedirects = 5;

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
        "x-goog-api-key", "x-api-key", "api-key",
    };

    /// <summary>Wraps <paramref name="inner"/> and forces it not to follow redirects by itself.</summary>
    public SafeRedirectHandler(HttpMessageHandler inner) : base(inner)
    {
        if (inner is HttpClientHandler http) http.AllowAutoRedirect = false;
        if (inner is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var current = request.RequestUri;
        if (current == null || !NetworkPolicy.IsAllowedDownloadUri(current))
            throw new HttpRequestException("Refusing a non-HTTPS URL.");

        for (var hop = 0; ; hop++)
        {
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode)) return response;
            // POST with an API key or a prompt body must not be replayed onto a Location we do not own.
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
                return response;
            if (hop >= MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("Too many redirects.");
            }
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null)
                throw new HttpRequestException("Redirect without Location.");
            if (!location.IsAbsoluteUri)
                location = new Uri(current, location);
            if (!NetworkPolicy.IsAllowedDownloadUri(location))
                throw new HttpRequestException("Refusing a redirect to a non-HTTPS URL.");

            var next = new HttpRequestMessage(request.Method, location);
            CopyHeaders(request, next, SameOrigin(current, location));
            request.Dispose();
            request = next;
            current = location;
        }
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static void CopyHeaders(HttpRequestMessage from, HttpRequestMessage to, bool sameOrigin)
    {
        foreach (var header in from.Headers)
        {
            if (!sameOrigin && SensitiveHeaders.Contains(header.Key)) continue;
            to.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        (int)code is 301 or 302 or 303 or 307 or 308;
}
