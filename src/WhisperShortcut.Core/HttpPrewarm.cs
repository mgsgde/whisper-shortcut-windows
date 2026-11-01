namespace WhisperShortcut.Core;

/// <summary>
/// Opens a TLS session to a cloud API while the user is still speaking. The handshake (often
/// 100–300 ms) then no longer sits on the Stop → first request path. Failures are ignored.
/// </summary>
internal static class HttpPrewarm
{
    /// <summary>HEAD <paramref name="origin"/>; logs outcome, never throws to the caller.</summary>
    public static Task Connect(HttpClient http, string origin, string label) =>
        Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var request = new HttpRequestMessage(HttpMethod.Head, origin);
                request.Headers.ExpectContinue = false;
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                Log.Info($"{label} prewarm HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                Log.Info($"{label} prewarm skipped ({ex.GetType().Name})");
            }
        });
}
