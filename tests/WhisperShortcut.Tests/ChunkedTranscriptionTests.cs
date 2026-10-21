using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ChunkedTranscriptionTests
{
    [Fact]
    public async Task OneChunk_IsReturnedUnchanged()
    {
        var text = await ChunkedTranscription.TranscribeAsync(
            new[] { new byte[] { 1 } },
            (_, _) => Task.FromResult("  hi  "),
            maxParallel: 4,
            CancellationToken.None);
        Assert.Equal("  hi  ", text);
    }

    [Fact]
    public async Task KeepsOrder_WhenLaterChunksFinishFirst()
    {
        var chunks = new[] { new byte[] { 0 }, new byte[] { 1 }, new byte[] { 2 } };
        var release = new TaskCompletionSource[3];
        var done = new TaskCompletionSource[3];
        for (var i = 0; i < 3; i++)
        {
            release[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            done[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var allIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var order = new List<int>();

        async Task<string> Transcribe(byte[] chunk, CancellationToken ct)
        {
            var id = chunk[0];
            if (Interlocked.Increment(ref entered) == chunks.Length) allIn.TrySetResult();
            await release[id].Task.WaitAsync(ct);
            lock (order) order.Add(id);
            done[id].TrySetResult();
            return $"t{id}";
        }

        var pending = ChunkedTranscription.TranscribeAsync(chunks, Transcribe, maxParallel: 3, CancellationToken.None);
        try
        {
            await allIn.Task.WaitAsync(TimeSpan.FromSeconds(2));
            release[2].SetResult();
            await done[2].Task.WaitAsync(TimeSpan.FromSeconds(2));
            release[1].SetResult();
            await done[1].Task.WaitAsync(TimeSpan.FromSeconds(2));
            release[0].SetResult();
        }
        finally
        {
            foreach (var gate in release) gate.TrySetResult();
        }
        var text = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { 2, 1, 0 }, order);
        Assert.Equal("t0 t1 t2", text);
    }

    [Fact]
    public async Task MaxParallel_IsRespected()
    {
        const int count = 6;
        const int limit = 2;
        var chunks = Enumerable.Range(0, count).Select(i => new byte[] { (byte)i }).ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = 0;
        var max = 0;

        async Task<string> Transcribe(byte[] chunk, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            do
            {
                seen = Volatile.Read(ref max);
                if (now <= seen) break;
            }
            while (Interlocked.CompareExchange(ref max, now, seen) != seen);

            await release.Task.WaitAsync(ct);
            Interlocked.Decrement(ref current);
            return "x";
        }

        var pending = ChunkedTranscription.TranscribeAsync(chunks, Transcribe, limit, CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (Volatile.Read(ref current) < limit && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            // A broken semaphore would let the rest in during this pause.
            await Task.Delay(80);
            Assert.Equal(limit, Volatile.Read(ref current));
            Assert.Equal(limit, Volatile.Read(ref max));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(string.Join(" ", Enumerable.Repeat("x", count)), await pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Failure_CancelsTheRest_AndSurfacesTheOriginal()
    {
        var chunks = new[] { new byte[] { 0 }, new byte[] { 1 }, new byte[] { 2 } };
        var allIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var cancelled = 0;

        async Task<string> Transcribe(byte[] chunk, CancellationToken ct)
        {
            if (Interlocked.Increment(ref entered) == chunks.Length) allIn.TrySetResult();
            if (chunk[0] == 0)
            {
                await allIn.Task;
                throw new InvalidOperationException("boom");
            }
            try
            {
                // The cancel may already hit while waiting for the others, so count it there too.
                await allIn.Task.WaitAsync(ct);
                // Finite so a missed cancel cannot pin the test host. Long enough that a correct cancel wins.
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
            return "nope";
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ChunkedTranscription.TranscribeAsync(chunks, Transcribe, maxParallel: 3, CancellationToken.None));
        Assert.Equal("boom", ex.Message);
        Assert.Equal(2, cancelled);
    }

    [Fact]
    public async Task EmptyResults_AreSkipped()
    {
        var answers = new[] { "  hello ", "", "\n", "   ", "world  " };
        var chunks = Enumerable.Range(0, answers.Length).Select(i => new byte[] { (byte)i }).ToArray();

        var text = await ChunkedTranscription.TranscribeAsync(chunks,
            (chunk, _) => Task.FromResult(answers[chunk[0]]),
            maxParallel: 4,
            CancellationToken.None);

        Assert.Equal("hello world", text);
    }
}
