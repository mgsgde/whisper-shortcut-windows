using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace WhisperShortcut.Core;

/// <summary>
/// Runs a transcription over the pieces from <see cref="WavChunker"/> and joins them in order.
/// One failing piece cancels the rest: otherwise a 30-minute dictation would keep uploading
/// after the user already saw the error.
/// </summary>
public static class ChunkedTranscription
{
    /// <summary>
    /// Transcribes <paramref name="chunks"/>. A single chunk is returned as the delegate produced it.
    /// Several chunks run at most <paramref name="maxParallel"/> at once; trimmed non-empty texts are
    /// joined with one space, in the original order.
    /// </summary>
    public static async Task<string> TranscribeAsync(IReadOnlyList<byte[]> chunks,
        Func<byte[], CancellationToken, Task<string>> transcribe, int maxParallel, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var text = chunks.Count == 1
            ? await transcribe(chunks[0], ct).ConfigureAwait(false)
            : await TranscribeMany(chunks, transcribe, maxParallel, ct).ConfigureAwait(false);
        Log.Info($"Chunked: {chunks.Count} chunks, {sw.ElapsedMilliseconds} ms");
        return text;
    }

    private static async Task<string> TranscribeMany(IReadOnlyList<byte[]> chunks,
        Func<byte[], CancellationToken, Task<string>> transcribe, int maxParallel, CancellationToken ct)
    {
        if (chunks.Count == 0) return "";

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sem = new SemaphoreSlim(maxParallel);
        var gate = new object();
        Exception? first = null;
        var cancelledByUs = 0;

        var tasks = new Task<string>[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            var index = i;
            tasks[i] = Run(index);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // WhenAll surfaces whichever task is first in the array, often a cancellation we caused
            // ourselves. The failure the user should see is the original one.
            Exception? error;
            lock (gate) error = first;
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }

        var parts = new List<string>(chunks.Count);
        foreach (var task in tasks)
        {
            var part = (task.Result ?? "").Trim();
            if (part.Length > 0) parts.Add(part);
        }
        return string.Join(" ", parts);

        async Task<string> Run(int index)
        {
            var acquired = false;
            try
            {
                await sem.WaitAsync(linked.Token).ConfigureAwait(false);
                acquired = true;
                return await transcribe(chunks[index], linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Note(ex);
                throw;
            }
            finally
            {
                if (acquired) sem.Release();
            }
        }

        void Note(Exception ex)
        {
            // An OperationCanceledException from our own Cancel must not hide the real failure.
            // A cancel that arrived from the caller is real: ct is already signaled.
            if (ex is OperationCanceledException && Volatile.Read(ref cancelledByUs) != 0 && !ct.IsCancellationRequested)
                return;
            lock (gate) first ??= ex;
            if (Interlocked.Exchange(ref cancelledByUs, 1) == 0)
                linked.Cancel();
        }
    }
}
