using System.Runtime.CompilerServices;
using System.Text;

namespace WhisperShortcut.Core;

/// <summary>
/// One token for a whole streamed request: it fires when the server has been silent for the armed time, and when the
/// caller cancels (the link). Callers decide what counts as a sign of life and call <see cref="Rearm"/> for it.
/// </summary>
internal sealed class IdleWatchdog : IDisposable
{
    private readonly CancellationTokenSource _cts;

    /// <summary>Starts the clock: <paramref name="firstIdle"/> to the first sign of life.</summary>
    public IdleWatchdog(CancellationToken caller, TimeSpan firstIdle)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        _cts.CancelAfter(firstIdle);
    }

    /// <summary>Cancelled by the idle timeout or the caller; pass it to every await of the request.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Restarts the clock with <paramref name="idle"/>.</summary>
    public void Rearm(TimeSpan idle) => _cts.CancelAfter(idle);

    /// <inheritdoc />
    public void Dispose() => _cts.Dispose();
}

/// <summary>Server-sent events as the cloud APIs send them.</summary>
internal static class Sse
{
    /// <summary>
    /// The payload of every <c>data:</c> line, trimmed, empty ones skipped; comments and other fields are ignored.
    /// <c>[DONE]</c> is passed on: OpenAI's stream ends there, Gemini's parser skips it.
    /// </summary>
    /// <param name="stream">The response body.</param>
    /// <param name="ct">Token for the reads (the watchdog's).</param>
    /// <param name="onLine">Runs for every line read, data or not (a caller that treats keep-alives as life signs re-arms here).</param>
    public static async IAsyncEnumerable<string> DataPayloads(Stream stream, [EnumeratorCancellation] CancellationToken ct,
        Action? onLine = null)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            onLine?.Invoke();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line.Substring(5).Trim();
            if (payload.Length > 0) yield return payload;
        }
    }
}
