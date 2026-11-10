namespace WhisperShortcut.Core;

/// <summary>How often and how long a caller retries; the decision whether a failure is worth it lives on the exception.</summary>
/// <param name="MaxAttempts">Attempts for timeouts, network errors and 429s.</param>
/// <param name="MaxServerErrorAttempts">Attempts when the server itself is failing (5xx).</param>
/// <param name="RetryDelay">Base of the exponential backoff (tests set zero).</param>
/// <param name="RetryAfterBuffer">Added to the provider's own wait hint (Gemini RetryInfo: +2 s, OpenAI: none).</param>
internal sealed record RetryPolicy(int MaxAttempts, int MaxServerErrorAttempts, TimeSpan RetryDelay, TimeSpan RetryAfterBuffer)
{
    /// <summary>Dictation and Dictate Prompt: 5 attempts, 6 for 5xx.</summary>
    public static RetryPolicy OneShot(TimeSpan retryDelay, TimeSpan retryAfterBuffer) =>
        new(HttpRetry.MaxAttempts, HttpRetry.MaxServerErrorAttempts, retryDelay, retryAfterBuffer);
}

/// <summary>
/// The one retry loop of all cloud clients. <see cref="GeminiException.Retryable"/> (set where the failure is mapped)
/// says whether a second try can help; the attempt counts, transient-status list and backoff formula live here
/// so the clients cannot drift.
/// </summary>
internal static class HttpRetry
{
    /// <summary>Attempts for timeouts, network errors and 429s.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Attempts when the server itself is failing (5xx).</summary>
    public const int MaxServerErrorAttempts = 6;

    /// <summary>Ceiling of the exponential backoff (1.5 s, 3 s, 6 s, 12 s, then this).</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Longest Retry-After / RetryInfo delay we will wait before giving up. A longer wait is not
    /// worth blocking a dictation for.
    /// </summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    /// <summary>HTTP statuses that a second try can fix (rate limit or a failing server).</summary>
    public static bool IsTransientStatus(int code) => code is 429 or 500 or 502 or 503 or 504;

    /// <summary>
    /// Exponential backoff for attempt <paramref name="attempt"/> (1-based):
    /// <paramref name="retryDelay"/> * 2^(attempt-1), capped at <see cref="MaxBackoff"/>.
    /// </summary>
    public static TimeSpan Backoff(TimeSpan retryDelay, int attempt) =>
        TimeSpan.FromTicks((long)Math.Min(
            retryDelay.Ticks * Math.Pow(2, attempt - 1), MaxBackoff.Ticks));

    /// <summary>
    /// Runs <paramref name="attempt"/> (1-based) until it returns or fails for good. Only a <see cref="GeminiException"/>
    /// with <see cref="GeminiException.Retryable"/> is retried; anything else, and the caller's cancellation, passes through.
    /// The wait is the provider's own hint (plus the policy buffer; a hint longer than <see cref="MaxWait"/> is not worth
    /// waiting for) or the exponential backoff.
    /// </summary>
    /// <param name="attempt">One try; gets its number.</param>
    /// <param name="policy">Attempt counts and delays.</param>
    /// <param name="logTag">Prefix of the retry log line.</param>
    /// <param name="ct">Caller's token.</param>
    /// <param name="mayRetry">Extra veto after a failure, e.g. chat must not retry once text reached the user.</param>
    /// <param name="beforeRetry">Runs after the decision to retry, before the wait, with the failed attempt's number.</param>
    public static async Task<T> RunAsync<T>(Func<int, Task<T>> attempt, RetryPolicy policy, string logTag, CancellationToken ct,
        Func<bool>? mayRetry = null, Action<int>? beforeRetry = null)
    {
        for (var n = 1; ; n++)
        {
            ct.ThrowIfCancellationRequested();
            GeminiException failure;
            try
            {
                return await attempt(n).ConfigureAwait(false);
            }
            catch (GeminiException ex) when (ex.Retryable)
            {
                failure = ex;
            }

            if (mayRetry != null && !mayRetry()) throw failure;
            if (n >= (failure.ServerFault ? policy.MaxServerErrorAttempts : policy.MaxAttempts)) throw failure;
            TimeSpan delay;
            if (failure.RetryAfter is { } hint)
            {
                if (hint > MaxWait) throw failure;
                delay = hint + policy.RetryAfterBuffer;
            }
            else
            {
                delay = Backoff(policy.RetryDelay, n);
            }
            Log.Info($"{logTag}: attempt {n} failed ({failure.Kind}), retrying in {delay.TotalSeconds:0.#} s");
            beforeRetry?.Invoke(n);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
