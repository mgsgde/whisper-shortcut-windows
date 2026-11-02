namespace WhisperShortcut.Core;

/// <summary>
/// Shared retry policy for Gemini and OpenAI HTTP calls. Both clients keep their own request
/// construction and error mapping; only the attempt counts, transient-status list and backoff
/// formula live here so they cannot drift.
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
}
