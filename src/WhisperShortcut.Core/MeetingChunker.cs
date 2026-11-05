namespace WhisperShortcut.Core;

/// <summary>When a Live Meeting chunk ends. Defaults are the Mac app's values.</summary>
/// <param name="MaxChunk">A chunk never gets longer than this.</param>
/// <param name="MinChunk">A pause only ends a chunk that is at least this long.</param>
/// <param name="SilenceDuration">How long the level must stay below the threshold for an early end.</param>
/// <param name="SilenceThresholdDb">RMS level (dBFS) below which a meter window counts as silent.</param>
/// <param name="MeterWindow">Length of one level measurement.</param>
/// <param name="NoiseFloorMarginDb">
/// Null: the threshold is always <paramref name="SilenceThresholdDb"/>. Otherwise the threshold follows the measured
/// noise floor (a low percentile of every window fed so far): floor + this margin, never above
/// <paramref name="SilenceThresholdDb"/> and never below <paramref name="LowestThresholdDb"/>. A quiet microphone in a
/// quiet room then still tells its quiet speech from its pauses.
/// </param>
/// <param name="LowestThresholdDb">Lower bound of the relative threshold.</param>
public sealed record MeetingChunkPolicy(TimeSpan MaxChunk, TimeSpan MinChunk, TimeSpan SilenceDuration,
    float SilenceThresholdDb, TimeSpan MeterWindow, float? NoiseFloorMarginDb = null, float LowestThresholdDb = -60f)
{
    /// <summary>60 s max, 10 s min, 1.5 s of silence below -40 dB, 0.3 s windows.</summary>
    public static MeetingChunkPolicy Default { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(1.5), -40f, TimeSpan.FromSeconds(0.3));
}

/// <summary>One finished piece of the meeting audio.</summary>
/// <param name="Index">Running number, starting at the chunker's first index.</param>
/// <param name="StartSeconds">Start in the meeting, including the chunker's start offset.</param>
/// <param name="DurationSeconds">Length of the audio.</param>
/// <param name="Wav">16-bit mono WAV bytes.</param>
/// <param name="Silent">No meter window of the chunk ever reached the threshold, so there is nothing to transcribe.</param>
/// <param name="LoudestWindowDb">RMS level (dBFS) of the chunk's loudest meter window.</param>
public sealed record MeetingAudioChunk(int Index, double StartSeconds, double DurationSeconds, byte[] Wav, bool Silent,
    float LoudestWindowDb = MeetingChunker.FloorDb);

/// <summary>
/// Cuts a continuous 16-bit mono stream into chunks, deterministically by sample count (not wall clock).
/// Level is measured per meter window (RMS dB). A chunk ends when it reaches the max length, or when it is at
/// least the min length and the last windows were silent for the silence duration in a row.
/// Silent = no window of the chunk ever reached the threshold. Single-threaded: the caller serializes Write/Flush.
/// The threshold is fixed unless the policy sets <see cref="MeetingChunkPolicy.NoiseFloorMarginDb"/>.
/// </summary>
public sealed class MeetingChunker
{
    /// <summary>Lowest level the chunker tells apart; digital silence counts as this.</summary>
    public const float FloorDb = -100f;

    /// <summary>Percentile of all windows so far taken as the noise floor (pauses make up more than this in speech).</summary>
    public const double NoiseFloorPercentile = 0.10;

    /// <summary>Windows needed before the noise floor is trusted (1 s at 0.2 s windows).</summary>
    public const int NoiseFloorMinWindows = 5;

    private readonly MeetingChunkPolicy _policy;
    private readonly double _startOffset;
    private readonly int _maxSamples;
    private readonly int _minSamples;
    private readonly int _windowSamples;
    private readonly int _silenceWindows;
    private readonly int _flushMinSamples;

    private short[] _buffer;
    private int _chunkSamples;
    private long _chunkStartSamples;
    private long _totalSamples;
    private int _windowFill;
    private int _silentRun;
    private float _chunkLoudestDb = FloorDb;
    private int _nextIndex;
    // 1 dB bins from FloorDb to 0 dBFS over every full window fed: the noise floor without keeping the windows.
    private readonly int[] _levelBins = new int[(int)-FloorDb + 1];
    private int _levelWindows;

    /// <summary>Creates a chunker for <paramref name="sampleRate"/> Hz mono input.</summary>
    /// <param name="sampleRate">Samples per second.</param>
    /// <param name="policy">Null = <see cref="MeetingChunkPolicy.Default"/>.</param>
    /// <param name="startOffsetSeconds">Seconds already in the meeting before the first sample (a resumed meeting).</param>
    /// <param name="firstIndex">Index of the first chunk.</param>
    public MeetingChunker(int sampleRate = 16000, MeetingChunkPolicy? policy = null, double startOffsetSeconds = 0, int firstIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        SampleRate = sampleRate;
        _policy = policy ?? MeetingChunkPolicy.Default;
        _startOffset = startOffsetSeconds;
        _nextIndex = firstIndex;
        _maxSamples = Math.Max(1, (int)Math.Round(_policy.MaxChunk.TotalSeconds * sampleRate));
        _minSamples = (int)Math.Round(_policy.MinChunk.TotalSeconds * sampleRate);
        _windowSamples = Math.Max(1, (int)Math.Round(_policy.MeterWindow.TotalSeconds * sampleRate));
        var silenceSamples = (int)Math.Round(_policy.SilenceDuration.TotalSeconds * sampleRate);
        // Integer ceiling: floating-point division of 1.5 / 0.3 can land just above 5.
        _silenceWindows = Math.Max(1, (silenceSamples + _windowSamples - 1) / _windowSamples);
        _flushMinSamples = sampleRate / 2;
        _buffer = new short[Math.Min(_maxSamples, sampleRate * 10)];
    }

    /// <summary>Samples per second of the stream.</summary>
    public int SampleRate { get; }

    /// <summary>Seconds (incl. start offset) where the chunk being filled began.</summary>
    public double CurrentChunkStartSeconds => _startOffset + (double)_chunkStartSamples / SampleRate;

    /// <summary>
    /// The level (dBFS) below which a window counts as silent right now: the policy's fixed threshold, or with
    /// <see cref="MeetingChunkPolicy.NoiseFloorMarginDb"/> the noise floor plus that margin within the policy's bounds.
    /// </summary>
    public float SilenceThresholdDb
    {
        get
        {
            if (_policy.NoiseFloorMarginDb is not { } margin || _levelWindows < NoiseFloorMinWindows)
                return _policy.SilenceThresholdDb;
            var relative = NoiseFloorDb + margin;
            return Math.Min(_policy.SilenceThresholdDb, Math.Max(_policy.LowestThresholdDb, relative));
        }
    }

    /// <summary>
    /// The quiet level of the stream so far: the <see cref="NoiseFloorPercentile"/> of every full window, in whole dB.
    /// <see cref="FloorDb"/> before the first window.
    /// </summary>
    public float NoiseFloorDb
    {
        get
        {
            if (_levelWindows == 0) return FloorDb;
            var target = Math.Max(1, (int)Math.Ceiling(_levelWindows * NoiseFloorPercentile));
            var seen = 0;
            for (var i = 0; i < _levelBins.Length; i++)
            {
                seen += _levelBins[i];
                if (seen >= target) return FloorDb + i;
            }
            return 0f;
        }
    }

    private void RecordLevel(float db)
    {
        _levelBins[(int)Math.Floor(db - FloorDb)]++;
        _levelWindows++;
    }

    private static float Clamp(float db) => float.IsNaN(db) ? FloorDb : Math.Clamp(db, FloorDb, 0f);

    /// <summary>Total seconds fed, incl. start offset.</summary>
    public double ElapsedSeconds => _startOffset + (double)_totalSamples / SampleRate;

    /// <summary>
    /// Feeds samples and returns the chunks they completed, oldest first (usually none). A rotation may fall
    /// in the middle of the span: the rest belongs to the next chunk.
    /// </summary>
    public IReadOnlyList<MeetingAudioChunk> Write(ReadOnlySpan<short> samples)
    {
        List<MeetingAudioChunk>? done = null;
        while (samples.Length > 0)
        {
            var n = Math.Min(samples.Length, Math.Min(_windowSamples - _windowFill, _maxSamples - _chunkSamples));
            Append(samples[..n]);
            samples = samples[n..];
            _totalSamples += n;

            var rotate = false;
            if (_chunkSamples >= _maxSamples)
            {
                rotate = true;
            }
            else if (_windowFill >= _windowSamples)
            {
                var db = Clamp(MeetingAudioMixer.RmsDb(_buffer.AsSpan(_chunkSamples - _windowFill, _windowFill)));
                _windowFill = 0;
                RecordLevel(db);
                if (db > _chunkLoudestDb) _chunkLoudestDb = db;
                if (db >= SilenceThresholdDb)
                {
                    _silentRun = 0;
                }
                else
                {
                    _silentRun++;
                }
                rotate = _chunkSamples >= _minSamples && _silentRun >= _silenceWindows;
            }

            if (rotate) (done ??= new()).Add(Complete());
        }
        return done ?? (IReadOnlyList<MeetingAudioChunk>)Array.Empty<MeetingAudioChunk>();
    }

    /// <summary>Ends the current chunk (stop). Null when it holds less than 0.5 s of audio.</summary>
    public MeetingAudioChunk? Flush()
    {
        if (_chunkSamples < _flushMinSamples)
        {
            ResetChunk();
            return null;
        }
        return Complete();
    }

    private void Append(ReadOnlySpan<short> samples)
    {
        var needed = _chunkSamples + samples.Length;
        if (needed > _buffer.Length)
            Array.Resize(ref _buffer, Math.Min(_maxSamples, Math.Max(needed, _buffer.Length * 2)));
        samples.CopyTo(_buffer.AsSpan(_chunkSamples));
        _chunkSamples += samples.Length;
        _windowFill += samples.Length;
    }

    private MeetingAudioChunk Complete()
    {
        // A partial last window still counts as sound if it is loud.
        if (_windowFill > 0)
        {
            var partial = Clamp(MeetingAudioMixer.RmsDb(_buffer.AsSpan(_chunkSamples - _windowFill, _windowFill)));
            if (partial > _chunkLoudestDb) _chunkLoudestDb = partial;
        }

        // Judged with the threshold at the end: a relative threshold may only have settled after the chunk began.
        var span = _buffer.AsSpan(0, _chunkSamples);
        var chunk = new MeetingAudioChunk(_nextIndex++, CurrentChunkStartSeconds, (double)_chunkSamples / SampleRate,
            Wav.Encode16(span, SampleRate), _chunkLoudestDb < SilenceThresholdDb, _chunkLoudestDb);
        ResetChunk();
        return chunk;
    }

    private void ResetChunk()
    {
        _chunkStartSamples = _totalSamples;
        _chunkSamples = 0;
        _windowFill = 0;
        _silentRun = 0;
        _chunkLoudestDb = FloorDb;
    }
}
