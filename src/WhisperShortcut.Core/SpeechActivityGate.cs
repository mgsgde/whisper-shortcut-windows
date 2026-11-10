using System.Diagnostics;

namespace WhisperShortcut.Core;

/// <summary>
/// Silero VAD in front of cloud dictation (Mac SpeechActivityGate). A chunk that is noise or a long
/// pause used to be uploaded anyway and came back as the glossary. This drops a chunk with almost no
/// speech and trims a long quiet head or tail. It never blocks: until the model is loaded, when it
/// failed to load, for anything but 16 kHz mono 16-bit PCM, and when a trim fails, the original goes
/// out. Offline engines never come through here.
/// </summary>
public static class SpeechActivityGate
{
    private enum LoadState { NotStarted, Loading, Ready, Failed }

    private static readonly object Sync = new();
    private static LoadState _state = LoadState.NotStarted;
    private static SileroVad? _vad;

    /// <summary>Env WSW_SPEECH_GATE=0 switches the gate off (kill switch, no Settings row; Mac: speechActivityGateEnabled).</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("WSW_SPEECH_GATE") != "0";

    /// <summary>True once the model is loaded and decisions are made.</summary>
    public static bool IsReady
    {
        get { lock (Sync) return _state == LoadState.Ready; }
    }

    /// <summary>
    /// Makes sure the model file exists (downloads it once, pinned by SHA-256) and loads it. Runs once
    /// per launch; a failure is logged once and the gate stays off. Never throws.
    /// </summary>
    public static async Task PrepareAsync(ModelDownloader downloader, CancellationToken ct = default)
    {
        lock (Sync)
        {
            if (_state != LoadState.NotStarted) return;
            _state = LoadState.Loading;
        }
        var watch = Stopwatch.StartNew();
        try
        {
            var component = OfflineCatalog.SileroVad;
            if (!ModelDownloader.IsInstalled(component))
                await downloader.DownloadAsync(component, null, ct).ConfigureAwait(false);
            var path = Path.Combine(component.FullPath, component.Files[0].RelativePath);
            var vad = await Task.Run(() => SileroVad.Load(path), ct).ConfigureAwait(false);
            lock (Sync) { _vad = vad; _state = LoadState.Ready; }
            Log.Info($"SPEECH-GATE: Silero VAD ready ({watch.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            lock (Sync) _state = LoadState.Failed;
            Log.Warn($"SPEECH-GATE: Silero VAD not available, uploading as before: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the gate to one recording or chunk. Returns null for no speech (do not upload), the same
    /// array to upload as is, or a trimmed copy. Never throws, never waits for the download.
    /// </summary>
    public static byte[]? Apply(byte[] wav)
    {
        if (!Enabled) return wav;
        SileroVad? vad;
        lock (Sync) vad = _state == LoadState.Ready ? _vad : null;
        return vad == null ? wav : Apply(wav, vad.Detect);
    }

    /// <summary>The gate with an injectable detector (tests run it without the model).</summary>
    internal static byte[]? Apply(byte[] wav, Func<float[], IReadOnlyList<SpeechSpan>> detect)
    {
        var watch = Stopwatch.StartNew();
        SpeechDecision decision;
        double duration;
        IReadOnlyList<SpeechSpan> segments;
        try
        {
            if (!Wav.TryGetPcm16(wav, out var pcm) || pcm.SampleRate != SileroVad.SampleRate || pcm.Channels != 1)
                return wav; // the microphone and the chunker produce 16 kHz mono; anything else keeps the old behaviour
            var samples = Wav.ReadMonoFloat(wav, out _);
            duration = (double)samples.Length / SileroVad.SampleRate;
            segments = detect(samples);
            decision = SpeechActivity.Decide(segments, duration);
        }
        catch (Exception ex)
        {
            Log.Warn($"SPEECH-GATE: could not classify audio: {ex.GetType().Name}: {ex.Message}");
            return wav;
        }

        var speech = segments.Sum(s => s.Length);
        Log.Info(FormattableString.Invariant(
            $"SPEECH-GATE: {decision.Label} (audio {duration:0.0}s, speech {speech:0.0}s, {segments.Count} segments, {watch.ElapsedMilliseconds}ms)"));

        switch (decision.Verdict)
        {
            case SpeechVerdict.NoSpeech:
                return null;
            case SpeechVerdict.Trim:
                try { return SpeechActivity.TrimWav(wav, decision.Start, decision.End); }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
                {
                    Log.Warn($"SPEECH-GATE: trim failed, uploading the original: {ex.GetType().Name}");
                    return wav;
                }
            default:
                return wav;
        }
    }
}
