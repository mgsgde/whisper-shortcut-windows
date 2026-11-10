using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>The one retry loop: it decides from the exception, the policy only carries numbers.</summary>
public class HttpRetryTests
{
    private static readonly RetryPolicy Fast = RetryPolicy.OneShot(TimeSpan.Zero, TimeSpan.Zero);

    private static GeminiException Failure(bool retryable, bool serverFault = false, TimeSpan? retryAfter = null) =>
        new(GeminiErrorKind.Server, "x") { Retryable = retryable, ServerFault = serverFault, RetryAfter = retryAfter };

    private static async Task<int> Calls(Func<int, Task<int>> attempt, RetryPolicy policy, Func<bool>? mayRetry = null, Action<int>? before = null)
    {
        var n = 0;
        try
        {
            await HttpRetry.RunAsync(i => { n++; return attempt(i); }, policy, "T", default, mayRetry, before);
        }
        catch (GeminiException) { }
        return n;
    }

    [Fact]
    public async Task ReturnsTheFirstSuccess_AfterRetryableFailures()
    {
        var result = await HttpRetry.RunAsync(i => i < 3 ? throw Failure(true) : Task.FromResult(i), Fast, "T", default);
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task ANonRetryableFailure_PassesThroughAtOnce()
    {
        Assert.Equal(1, await Calls(_ => throw Failure(false), Fast));
        var n = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HttpRetry.RunAsync<int>(_ => { n++; throw new InvalidOperationException("other"); }, Fast, "T", default));
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task AttemptLimits_FiveGenerally_SixWhenTheServerFails()
    {
        Assert.Equal(5, await Calls(_ => throw Failure(true), Fast));
        Assert.Equal(6, await Calls(_ => throw Failure(true, serverFault: true), Fast));
        Assert.Equal(3, await Calls(_ => throw Failure(true, serverFault: true), new RetryPolicy(3, 3, TimeSpan.Zero, TimeSpan.Zero)));
    }

    [Fact]
    public async Task MayRetry_VetoesTheRetry_BeforeRetryRunsOnlyWhenRetrying()
    {
        var vetoed = new List<int>();
        Assert.Equal(1, await Calls(_ => throw Failure(true), Fast, mayRetry: () => false, before: vetoed.Add));
        Assert.Empty(vetoed);

        var seen = new List<int>();
        Assert.Equal(5, await Calls(_ => throw Failure(true), Fast, before: seen.Add));
        Assert.Equal(new[] { 1, 2, 3, 4 }, seen);
    }

    [Fact]
    public async Task RetryAfter_LongerThanAMinute_GivesUp_ShorterIsWaitedForPlusBuffer()
    {
        Assert.Equal(1, await Calls(_ => throw Failure(true, retryAfter: TimeSpan.FromSeconds(61)), Fast));
        // A zero hint plus a zero buffer retries immediately, up to the attempt limit.
        Assert.Equal(5, await Calls(_ => throw Failure(true, retryAfter: TimeSpan.Zero), Fast));
    }

    [Fact]
    public async Task TheCallersCancellation_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var n = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HttpRetry.RunAsync(_ => { n++; return Task.FromResult(1); }, Fast, "T", cts.Token));
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task Cancellation_DuringTheWait_Stops()
    {
        using var cts = new CancellationTokenSource(100);
        var slow = new RetryPolicy(5, 5, TimeSpan.FromSeconds(30), TimeSpan.Zero);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HttpRetry.RunAsync<int>(_ => throw Failure(true), slow, "T", cts.Token));
    }
}
