namespace WhisperShortcut.Core;

/// <summary>
/// A growing PCM timeline for Read Aloud with pitch-preserving speed. Audio is appended while synthesis is still running; the
/// player reads float samples at a chosen speed. The Mac plays through AVAudioUnitTimePitch; here a WSOLA time stretcher
/// (waveform-similarity overlap-add) does the same job: speed changes the tempo, not the voice's pitch.
/// </summary>
/// <remarks>
/// Thread-safe: <see cref="Append"/> comes from network threads, <see cref="Read"/> from the audio thread,
/// <see cref="Seek"/> and <see cref="Speed"/> from the UI thread. At speed 1 the samples pass through unchanged.
/// </remarks>
public sealed class SpeechPlaybackBuffer
{
    /// <summary>Sample rate of the timeline (Hz). PCM is s16le mono.</summary>
    public const int SampleRate = 24000;

    // WSOLA geometry: 40 ms frames, synthesis hop of half a frame, +-10 ms search for the best-fitting frame start.
    private const int Frame = 960;
    private const int Hop = Frame / 2;
    private const int SearchRadius = 240;

    private const double MinSpeed = 0.5;
    private const double MaxSpeed = 2.5;

    // Periodic Hann: window[n] + window[n + Hop] == 1, so overlapped frames add up to unity gain.
    private static readonly float[] Window = BuildWindow();

    private readonly object _lock = new();
    private short[] _samples = new short[SampleRate * 4];
    private int _count;
    private int _oddByte = -1;
    private bool _complete;
    private double _speed = 1.0;
    private double _estimatedTotal;

    // Source position of the next sample the consumer will read, in source samples.
    private double _srcPos;

    // Stretcher state (reset by Seek and by a speed change).
    private double _nominal;                 // nominal analysis position in source samples
    private bool _hasPrev;                   // a frame was already placed, so there is a tail to overlap
    private readonly float[] _tail = new float[Hop];     // windowed second half of the last frame
    private readonly float[] _natural = new float[Hop];  // raw second half of the last frame: what the signal does next
    private readonly float[] _out = new float[Hop];      // output of the last step
    private int _outPos;
    private int _outLen;
    private readonly float[] _segment = new float[Frame + 2 * SearchRadius];

    private static float[] BuildWindow()
    {
        var w = new float[Frame];
        for (var n = 0; n < Frame; n++) w[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / Frame));
        return w;
    }

    /// <summary>Appends s16le mono PCM. An odd trailing byte is kept and completed by the next call.</summary>
    public void Append(byte[] pcm16le)
    {
        if (pcm16le == null || pcm16le.Length == 0) return;
        lock (_lock)
        {
            var i = 0;
            if (_oddByte >= 0)
            {
                Add((short)(_oddByte | (pcm16le[0] << 8)));
                _oddByte = -1;
                i = 1;
            }
            for (; i + 1 < pcm16le.Length; i += 2) Add((short)(pcm16le[i] | (pcm16le[i + 1] << 8)));
            if (i < pcm16le.Length) _oddByte = pcm16le[i];
        }
    }

    private void Add(short sample)
    {
        if (_count == _samples.Length) Array.Resize(ref _samples, _samples.Length * 2);
        _samples[_count++] = sample;
    }

    /// <summary>Tells the buffer that no more audio will come: the end is exact from now on.</summary>
    public void MarkComplete()
    {
        lock (_lock) _complete = true;
    }

    /// <summary>True once <see cref="MarkComplete"/> was called.</summary>
    public bool IsComplete
    {
        get { lock (_lock) return _complete; }
    }

    /// <summary>Playback speed, 0.5 to 2.5. 1.0 passes the samples through exactly; a change resets the stretcher at the current position.</summary>
    public double Speed
    {
        get { lock (_lock) return _speed; }
        set
        {
            lock (_lock)
            {
                var v = Math.Clamp(double.IsNaN(value) ? 1.0 : value, MinSpeed, MaxSpeed);
                if (Math.Abs(v - _speed) < 1e-9) return;
                _speed = v;
                ResetStretcher();
            }
        }
    }

    /// <summary>Playback position in source time (seconds of the original audio).</summary>
    public double PositionSeconds
    {
        get { lock (_lock) return Math.Min(_srcPos, _count) / SampleRate; }
    }

    /// <summary>Seconds of source audio received so far.</summary>
    public double BufferedSeconds
    {
        get { lock (_lock) return (double)_count / SampleRate; }
    }

    /// <summary>Estimated total length while synthesis runs (the app derives it from the text length).</summary>
    public double EstimatedTotalSeconds
    {
        get { lock (_lock) return _estimatedTotal; }
        set { lock (_lock) _estimatedTotal = Math.Max(0, value); }
    }

    /// <summary>Total length in source seconds: exact once complete, before that the larger of the estimate and what has arrived.</summary>
    public double DurationSeconds
    {
        get
        {
            lock (_lock)
            {
                var buffered = (double)_count / SampleRate;
                return _complete ? buffered : Math.Max(_estimatedTotal, buffered);
            }
        }
    }

    /// <summary>Jumps to <paramref name="seconds"/> (source time), clamped to what has arrived; resets the stretcher.</summary>
    public void Seek(double seconds)
    {
        lock (_lock)
        {
            if (double.IsNaN(seconds)) seconds = 0;
            _srcPos = Math.Clamp(seconds * SampleRate, 0, _count);
            ResetStretcher();
        }
    }

    /// <summary>Seeks relative to the current position.</summary>
    public void SkipBy(double seconds) => Seek(PositionSeconds + seconds);

    /// <summary>Complete and everything, including the stretcher's tail, has been read.</summary>
    public bool IsFinished
    {
        get
        {
            lock (_lock)
            {
                if (!_complete) return false;
                if (IsPassThrough) return Math.Round(_srcPos) >= _count;
                return _outPos >= _outLen && !_hasPrev && (long)Math.Round(_nominal) >= _count;
            }
        }
    }

    /// <summary>Not complete, and the next samples have not arrived yet (the player should show "buffering").</summary>
    public bool IsStarved
    {
        get
        {
            lock (_lock)
            {
                if (_complete) return false;
                if (IsPassThrough) return Math.Round(_srcPos) >= _count;
                return _outPos >= _outLen && !CanProduce();
            }
        }
    }

    private bool IsPassThrough => Math.Abs(_speed - 1.0) < 1e-9;

    private void ResetStretcher()
    {
        _nominal = _srcPos;
        _hasPrev = false;
        _outPos = _outLen = 0;
    }

    /// <summary>
    /// Fills up to <paramref name="count"/> samples (-1..1) at <see cref="Speed"/> and returns how many were written; the caller
    /// zero-fills the rest. Returns less than <paramref name="count"/> only when starved or finished.
    /// </summary>
    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            var n = 0;
            if (IsPassThrough)
            {
                var from = (int)Math.Round(_srcPos);
                n = Math.Max(0, Math.Min(count, _count - from));
                for (var i = 0; i < n; i++) buffer[offset + i] = _samples[from + i] / 32768f;
                _srcPos += n;
                return n;
            }

            while (n < count)
            {
                if (_outPos >= _outLen && !ProduceStep()) break;
                var take = Math.Min(count - n, _outLen - _outPos);
                Array.Copy(_out, _outPos, buffer, offset + n, take);
                _outPos += take;
                n += take;
            }
            _srcPos = Math.Min(_srcPos + n * _speed, Math.Max(_count, 0));
            return n;
        }
    }

    // True when ProduceStep would find enough audio to place the next frame (or has a tail/end to flush).
    private bool CanProduce()
    {
        var a = (long)Math.Round(_nominal);
        if (_complete) return a < _count || _hasPrev;
        var need = a + Frame + (_hasPrev ? SearchRadius : 0);
        return _count >= need;
    }

    private float Sample(long index) => index >= 0 && index < _count ? _samples[index] / 32768f : 0f;

    // One WSOLA step: picks the frame start near the nominal position that continues the previous frame best, overlap-adds it,
    // and leaves Hop finished samples in _out. False when starved or when the end of the audio is reached.
    private bool ProduceStep()
    {
        var a = (long)Math.Round(_nominal);
        if (_complete && a >= _count)
        {
            // End of audio: the last frame's windowed tail is the fade-out. Hand it out once.
            if (!_hasPrev) return false;
            Array.Copy(_tail, _out, Hop);
            _outPos = 0;
            _outLen = Hop;
            _hasPrev = false;
            return true;
        }
        if (!CanProduce()) return false;

        // Local copy of the search area: source samples from a - radius to a + radius + frame (zero beyond the audio).
        var baseIndex = a - SearchRadius;
        for (var j = 0; j < _segment.Length; j++) _segment[j] = Sample(baseIndex + j);

        var start = SearchRadius; // offset into the segment of the chosen frame
        if (_hasPrev)
        {
            start = BestOffset(a);
        }

        // Overlap-add: the previous frame's tail plus this frame's first half. The very first frame has no tail and goes out
        // raw, so playback and every seek start at full volume instead of fading in.
        for (var i = 0; i < Hop; i++)
        {
            var x = _segment[start + i];
            _out[i] = _hasPrev ? _tail[i] + Window[i] * x : x;
        }
        for (var i = 0; i < Hop; i++)
        {
            var x = _segment[start + Hop + i];
            _tail[i] = Window[Hop + i] * x;
            _natural[i] = x;
        }
        _hasPrev = true;
        _outPos = 0;
        _outLen = Hop;
        _nominal += Hop * _speed;
        return true;
    }

    // Offset into _segment (0..2*radius) whose first half correlates best with where the previous frame was heading
    // (normalised cross-correlation, every second sample to keep the audio thread cheap). A tie keeps the centre, so silence
    // does not drift.
    private int BestOffset(long a)
    {
        var naturalEnergy = 0.0;
        for (var j = 0; j < Hop; j += 2) naturalEnergy += _natural[j] * _natural[j];

        var best = SearchRadius;
        var bestScore = Score(SearchRadius, naturalEnergy);
        for (var off = 0; off <= 2 * SearchRadius; off++)
        {
            if (off == SearchRadius || a - SearchRadius + off < 0) continue;
            var score = Score(off, naturalEnergy);
            // Prefer the candidate closer to the nominal position unless clearly better.
            if (score > bestScore + 1e-6)
            {
                bestScore = score;
                best = off;
            }
        }
        return best;
    }

    private double Score(int off, double naturalEnergy)
    {
        double dot = 0, energy = 0;
        for (var j = 0; j < Hop; j += 2)
        {
            var v = _segment[off + j];
            dot += v * _natural[j];
            energy += v * v;
        }
        return dot / Math.Sqrt(energy * naturalEnergy + 1e-9);
    }
}
