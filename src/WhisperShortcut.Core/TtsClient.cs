using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisperShortcut.Core;

/// <summary>
/// Cuts a byte stream of s16le mono PCM into playable slices (Mac PCMStreamBatcher): never mid-sample, a short first slice
/// so playback starts as early as possible, then half-second batches so the player is not fed a flood of tiny buffers.
/// 24 kHz x 2 bytes = 48 000 bytes per second.
/// </summary>
internal sealed class PcmStreamBatcher
{
    public const int BytesPerSecond = 48_000;

    private readonly int _firstFlushBytes;
    private readonly int _flushBytes;
    private readonly List<byte> _pending = new();
    private bool _flushedOnce;

    public PcmStreamBatcher(double firstFlushSeconds = 0.25, double flushSeconds = 0.5)
    {
        // Even thresholds: a slice never ends in the middle of a 16-bit sample.
        _firstFlushBytes = (int)(firstFlushSeconds * BytesPerSecond) & ~1;
        _flushBytes = (int)(flushSeconds * BytesPerSecond) & ~1;
    }

    /// <summary>Adds bytes; every complete batch goes to <paramref name="emit"/> (awaited, in order).</summary>
    public async Task AddAsync(ReadOnlyMemory<byte> data, Func<byte[], Task> emit)
    {
        _pending.AddRange(data.ToArray());
        while (true)
        {
            var threshold = _flushedOnce ? _flushBytes : _firstFlushBytes;
            if (_pending.Count < threshold) return;
            var slice = _pending.GetRange(0, threshold).ToArray();
            _pending.RemoveRange(0, threshold);
            _flushedOnce = true;
            await emit(slice).ConfigureAwait(false);
        }
    }

    /// <summary>What is left at the end of the stream (a trailing half sample is dropped); null when nothing is left.</summary>
    public byte[]? Drain()
    {
        if (_pending.Count % 2 != 0) _pending.RemoveAt(_pending.Count - 1);
        if (_pending.Count == 0) return null;
        var rest = _pending.ToArray();
        _pending.Clear();
        _flushedOnce = true;
        return rest;
    }
}

/// <summary>
/// Playback-order gate (Mac PlaybackOrderGate): chunks finish out of order, so audio is held here until every chunk before
/// it has been emitted. Partial audio (a streaming provider's slices) passes straight through for the chunk whose turn it
/// is and is buffered for the others; on completion only the bytes not yet emitted go out, so a chunk that streamed live is
/// never played twice. A chunk that failed for good opens the gate for its successors: playing the rest with a gap beats
/// playing nothing. Lock based; <c>emit</c> runs inside the lock, so calls are in order and never concurrent.
/// </summary>
public sealed class PlaybackOrderGate
{
    private readonly object _lock = new();
    private readonly int _total;
    private readonly Action<byte[]> _emit;
    private int _next;

    // Audio received for chunks that are not yet at the gate, live partials included.
    private readonly Dictionary<int, List<byte>> _buffered = new();
    // How many bytes of each chunk were already handed out.
    private readonly Dictionary<int, int> _emitted = new();
    private readonly HashSet<int> _completed = new();
    private readonly HashSet<int> _failed = new();

    /// <summary>Creates a gate for <paramref name="totalChunks"/> chunks; <paramref name="emit"/> receives PCM in playback order.</summary>
    public PlaybackOrderGate(int totalChunks, Action<byte[]> emit)
    {
        _total = totalChunks;
        _emit = emit;
    }

    /// <summary>A streaming slice of chunk <paramref name="index"/>, in arrival order.</summary>
    public void Partial(int index, byte[] data)
    {
        lock (_lock)
        {
            if (_completed.Contains(index) || _failed.Contains(index)) return;
            if (index == _next)
            {
                Emit(data, index);
                return;
            }
            if (!_buffered.TryGetValue(index, out var list)) _buffered[index] = list = new List<byte>();
            list.AddRange(data);
        }
    }

    /// <summary>Drops partials buffered for <paramref name="index"/> that were never emitted; a retry resends them. Bytes already played stay counted.</summary>
    public void DiscardBuffered(int index)
    {
        lock (_lock) _buffered.Remove(index);
    }

    /// <summary>The complete audio of chunk <paramref name="index"/>. Emits the remainder (if its turn) and what became contiguous behind it.</summary>
    public void Complete(int index, byte[] data)
    {
        lock (_lock)
        {
            _completed.Add(index);
            _buffered[index] = new List<byte>(data);
            ReleasePlayable();
        }
    }

    /// <summary>Chunk <paramref name="index"/> failed for good: its successors play on.</summary>
    public void Fail(int index)
    {
        lock (_lock)
        {
            _failed.Add(index);
            _buffered.Remove(index);
            ReleasePlayable();
        }
    }

    private void ReleasePlayable()
    {
        while (_next < _total)
        {
            var index = _next;
            if (_completed.Contains(index))
            {
                _buffered.Remove(index, out var list);
                var already = _emitted.GetValueOrDefault(index);
                if (list != null && list.Count > already) Emit(list.GetRange(already, list.Count - already).ToArray(), index);
                _next++;
            }
            else if (_failed.Contains(index))
            {
                _next++;
            }
            else
            {
                // Not finished yet, but its turn: flush what has streamed in so far and wait.
                if (_buffered.Remove(index, out var head) && head.Count > 0) Emit(head.ToArray(), index);
                break;
            }
        }
    }

    private void Emit(byte[] data, int index)
    {
        if (data.Length == 0) return;
        _emitted[index] = _emitted.GetValueOrDefault(index) + data.Length;
        _emit(data);
    }
}

/// <summary>
/// Read Aloud speech synthesis against Gemini TTS (streamGenerateContent over SSE) and OpenAI (/v1/audio/speech).
/// Returns raw PCM s16le, 24 kHz, mono. Texts are split into chunks (see <see cref="TtsTextChunker"/>) that are synthesized in
/// parallel, retried per chunk, and handed to the player strictly in playback order. Keys are read at call time.
/// </summary>
public sealed class TtsClient
{
    // The text goes in bare: Gemini 3.8 TTS speaks any lead-in literally ("Sag Folgendes: ..."), measured on the Mac.
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly Func<string?> _geminiKey;
    private readonly Func<string?> _openAiKey;

    /// <summary>Base of the exponential backoff between retries of a chunk (1.5 s, doubling; tests set it to zero).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Safety margin added to a Google RetryInfo delay (2 s; tests set it to zero).</summary>
    public TimeSpan RetryInfoBuffer { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Idle budget before the first audio of a stream. The preview models were unstable on the Mac (first audio 0.9-3.3 s on a
    /// good run, up to 39 s on a bad one), so 45 s admits the slow-but-alive case and still aborts a dead request.
    /// </summary>
    internal TimeSpan FirstAudioTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Idle budget between audio objects once the stream runs. A stream that hangs mid-way is aborted and retried.</summary>
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Creates a client reading the keys from <paramref name="settings"/> on every call.</summary>
    public TtsClient(Settings settings, HttpClient? http = null)
        : this(() => settings.ApiKey, () => settings.OpenAiKey, http) { }

    /// <summary>Creates a client with explicit key providers (evaluated on every call).</summary>
    public TtsClient(Func<string?> geminiKey, Func<string?> openAiKey, HttpClient? http = null)
    {
        _geminiKey = geminiKey;
        _openAiKey = openAiKey;
        _http = http ?? NetworkPolicy.CreateApiClient();
    }

    internal static string BuildGeminiBody(string text, string voice) =>
        new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = text }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject
                    {
                        ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = voice },
                    },
                },
            },
        }.ToJsonString(Compact);

    internal static string BuildOpenAiBody(string model, string text, string voice) =>
        new JsonObject
        {
            ["model"] = model,
            ["input"] = text,
            ["voice"] = voice,
            ["response_format"] = "pcm",
        }.ToJsonString(Compact);

    /// <summary>
    /// One <c>streamGenerateContent</c> SSE object: the decoded PCM of every <c>inlineData</c> part (null when it has none) and
    /// the finish reason. Throws <see cref="GeminiException"/> for a top-level <c>error</c> and for a blocked prompt.
    /// Unparseable text counts as "no audio": the caller only hands over one <c>data:</c> line.
    /// </summary>
    internal static (byte[]? Audio, string? FinishReason) ParseGeminiStreamObject(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return (null, null); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            if (CloudApi.Gemini.StreamError(root, json, "TTS Gemini", "den Text zum Vorlesen") is { } streamError) throw streamError;

            string? finish = null;
            using var audio = new MemoryStream();
            if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array
                && candidates.GetArrayLength() > 0 && candidates[0].ValueKind == JsonValueKind.Object)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("finishReason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    finish = fr.GetString();
                if (candidate.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                    && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object) continue;
                        if (!part.TryGetProperty("inlineData", out var inline) && !part.TryGetProperty("inline_data", out inline)) continue;
                        if (inline.ValueKind != JsonValueKind.Object
                            || !inline.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) continue;
                        var b64 = data.GetString() ?? "";
                        var buffer = new byte[b64.Length * 3 / 4 + 3];
                        if (Convert.TryFromBase64String(b64, buffer, out var written)) audio.Write(buffer, 0, written);
                    }
                }
            }
            return (audio.Length == 0 ? null : audio.ToArray(), finish);
        }
    }

    /// <summary>
    /// Synthesizes one text segment with a single request (no retry) and returns the complete raw PCM (s16le, 24 kHz, mono).
    /// <paramref name="onPartial"/>, when given, receives playable slices in order and is awaited.
    /// </summary>
    public Task<byte[]> SynthesizeChunkAsync(TtsModel model, string voice, string text, Func<byte[], Task>? onPartial,
        CancellationToken ct) =>
        model.Provider == TtsProvider.Gemini
            ? SynthesizeGeminiAsync(model, voice, text, onPartial, ct)
            : SynthesizeOpenAiAsync(model, voice, text, onPartial, ct);

    private async Task<byte[]> SynthesizeGeminiAsync(TtsModel model, string voice, string text,
        Func<byte[], Task>? onPartial, CancellationToken ct)
    {
        var api = CloudApi.Gemini;
        var key = _geminiKey()?.Trim();
        if (string.IsNullOrEmpty(key)) throw api.NoKey(KeyHint.Accounts);

        var started = Stopwatch.StartNew();
        // The watchdog is re-armed on audio only (not on keep-alive lines): a stream that sends no sound is stalled.
        using var watchdog = new IdleWatchdog(ct, FirstAudioTimeout);
        Log.Info($"TTS {model.Id}: request ({text.Length} chars, voice {voice})");
        return await CloudHttp.SendAsync(_http, api, $"{api.ApiBase}{model.Id}:streamGenerateContent?alt=sse", key,
            new StringContent(BuildGeminiBody(text, voice), Encoding.UTF8, "application/json"), $"TTS {model.Id}",
            watchdog.Token, ct, async response =>
            {
                using var stream = await response.Content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false);
                var batcher = new PcmStreamBatcher();
                using var audio = new MemoryStream();
                var objects = 0;
                var audioObjects = 0;
                var loggedFirst = false;
                await foreach (var json in Sse.DataPayloads(stream, watchdog.Token).ConfigureAwait(false))
                {
                    if (json == "[DONE]") continue;
                    objects++;
                    var (pcm, finish) = ParseGeminiStreamObject(json);
                    if (finish != null && finish != "STOP") Log.Info($"TTS {model.Id}: finishReason {finish}");
                    if (pcm == null) continue;
                    audioObjects++;
                    watchdog.Rearm(StallTimeout);
                    audio.Write(pcm, 0, pcm.Length);
                    if (onPartial == null) continue;
                    await batcher.AddAsync(pcm, async slice =>
                    {
                        if (!loggedFirst)
                        {
                            loggedFirst = true;
                            Log.Info($"TTS {model.Id}: first audio slice after {started.ElapsedMilliseconds} ms ({slice.Length} bytes)");
                        }
                        await onPartial(slice).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                if (onPartial != null && batcher.Drain() is { } rest) await onPartial(rest).ConfigureAwait(false);

                Log.Info($"TTS {model.Id}: stream end after {started.ElapsedMilliseconds} ms (objects {objects}, audio objects {audioObjects}, {audio.Length} bytes)");
                if (audio.Length == 0)
                {
                    Log.Error($"TTS {model.Id}: 2xx without any audio (objects {objects})");
                    throw new GeminiException(api.Name, GeminiErrorKind.Server, "Gemini hat kein Audio zum Vorlesen geliefert.")
                        { Retryable = true }; // a new request may well deliver audio
                }
                return EvenLength(audio.ToArray());
            }, HttpCompletionOption.ResponseHeadersRead, "stalled, no audio progress for the allowed time").ConfigureAwait(false);
    }

    private async Task<byte[]> SynthesizeOpenAiAsync(TtsModel model, string voice, string text,
        Func<byte[], Task>? onPartial, CancellationToken ct)
    {
        var api = CloudApi.OpenAi;
        var key = _openAiKey()?.Trim();
        if (string.IsNullOrEmpty(key)) throw api.NoKey(KeyHint.Accounts);

        var started = Stopwatch.StartNew();
        using var watchdog = new IdleWatchdog(ct, FirstAudioTimeout);
        Log.Info($"TTS {model.Id}: request ({text.Length} chars, voice {voice})");
        return await CloudHttp.SendAsync(_http, api, api.ApiBase + "audio/speech", key,
            new StringContent(BuildOpenAiBody(model.Id, text, voice), Encoding.UTF8, "application/json"), $"TTS {model.Id}",
            watchdog.Token, ct, async response =>
            {
                using var stream = await response.Content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false);
                var batcher = new PcmStreamBatcher();
                using var audio = new MemoryStream();
                var buffer = new byte[8192];
                var loggedFirst = false;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(), watchdog.Token).ConfigureAwait(false)) > 0)
                {
                    watchdog.Rearm(StallTimeout);
                    audio.Write(buffer, 0, read);
                    if (onPartial == null) continue;
                    await batcher.AddAsync(buffer.AsMemory(0, read), async slice =>
                    {
                        if (!loggedFirst)
                        {
                            loggedFirst = true;
                            Log.Info($"TTS {model.Id}: first audio slice after {started.ElapsedMilliseconds} ms ({slice.Length} bytes)");
                        }
                        await onPartial(slice).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                if (onPartial != null && batcher.Drain() is { } rest) await onPartial(rest).ConfigureAwait(false);

                Log.Info($"TTS {model.Id}: stream end after {started.ElapsedMilliseconds} ms ({audio.Length} bytes)");
                if (audio.Length == 0)
                {
                    Log.Error($"TTS {model.Id}: 2xx without any audio");
                    throw new GeminiException(api.Name, GeminiErrorKind.Server, "OpenAI hat kein Audio zum Vorlesen geliefert.")
                        { Retryable = true };
                }
                return EvenLength(audio.ToArray());
            }, HttpCompletionOption.ResponseHeadersRead, "stalled, no audio progress for the allowed time").ConfigureAwait(false);
    }

    // A trailing half sample is noise at best; the batcher drops it from the partials too.
    private static byte[] EvenLength(byte[] pcm) => pcm.Length % 2 == 0 ? pcm : pcm[..^1];

    /// <summary>
    /// Synthesizes the whole text: split by <see cref="TtsTextChunker"/> for the model's provider, all chunks started at once,
    /// each retried on its own (<see cref="GeminiException.Retryable"/> failures, <see cref="HttpRetry.MaxAttempts"/> attempts). <paramref name="onAudio"/>
    /// receives PCM strictly in playback order, may be called from any thread, never concurrently. Returns how many chunks failed
    /// for good (0 = all fine; the rest plays with a gap). If every chunk failed, the first chunk's exception is thrown.
    /// Cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<int> SynthesizeAsync(TtsModel model, string voice, string text, Action<byte[]> onAudio, CancellationToken ct)
    {
        var chunks = TtsTextChunker.Split(text ?? "", ReadAloud.ChunkSizeFor(model.Provider));
        if (chunks.Count == 0) return 0;
        Log.Info($"TTS {model.Id}: {text!.Length} chars in {chunks.Count} chunks, voice {voice}");

        var gate = new PlaybackOrderGate(chunks.Count, onAudio);
        var started = Stopwatch.StartNew();
        var tasks = new Task<Exception?>[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            var index = i;
            tasks[i] = Task.Run(() => RunChunkAsync(model, voice, chunks[index], index, chunks.Count, gate, ct), CancellationToken.None);
        }
        var errors = await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var failed = errors.Count(e => e != null);
        Log.Info($"TTS {model.Id}: done after {started.ElapsedMilliseconds} ms, {failed} of {chunks.Count} chunks failed");
        if (failed == chunks.Count) ExceptionDispatchInfo.Capture(errors[0]!).Throw();
        return failed;
    }

    // One chunk with retries. Returns null on success, else the exception it failed with.
    private async Task<Exception?> RunChunkAsync(TtsModel model, string voice, string text, int index, int total,
        PlaybackOrderGate gate, CancellationToken ct)
    {
        var policy = new RetryPolicy(HttpRetry.MaxAttempts, HttpRetry.MaxAttempts, RetryDelay,
            model.Provider == TtsProvider.Gemini ? RetryInfoBuffer : TimeSpan.Zero);
        byte[] data;
        try
        {
            data = await HttpRetry.RunAsync(attempt =>
            {
                // Partials stream to the gate on the first attempt only. A retry re-synthesizes the whole chunk (TTS output
                // is not deterministic, so its bytes cannot be spliced onto what already played); the gate then emits just
                // the part beyond what it has handed out.
                Func<byte[], Task>? partial = attempt == 1
                    ? slice => { gate.Partial(index, slice); return Task.CompletedTask; }
                    : null;
                return SynthesizeChunkAsync(model, voice, text, partial, ct);
            }, policy, $"TTS {model.Id} chunk {index + 1}/{total}", ct, beforeRetry: _ => gate.DiscardBuffered(index)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"TTS {model.Id}: chunk {index + 1}/{total} failed for good", ex);
            gate.Fail(index);
            return ex;
        }
        gate.Complete(index, data);
        return null;
    }
}
