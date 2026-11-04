using NAudio.Wave;

namespace WhisperShortcut;

/// <summary>
/// Plays a <see cref="Core.SpeechPlaybackBuffer"/> on the default output device. The buffer grows while the speech is still
/// being synthesized; until the next audio arrives the player plays silence instead of stopping (Mac: the pill shows
/// buffering). Speed with the pitch kept comes from the buffer (WSOLA), so the device always runs at 24 kHz.
/// </summary>
internal sealed class TtsPlayer : IDisposable
{
    private readonly Core.SpeechPlaybackBuffer _buffer;
    private readonly WaveOutEvent _out = new() { DesiredLatency = 200 };
    private bool _disposed;

    /// <summary>Playback reached the end of a complete buffer, or the device failed. Raised on the UI thread.</summary>
    public event Action<Exception?>? Finished;

    public TtsPlayer(Core.SpeechPlaybackBuffer buffer)
    {
        _buffer = buffer;
        // WaveOut converts nothing by itself; 16-bit PCM is what every output device takes.
        _out.Init(new BufferSampleProvider(buffer), convertTo16Bit: true);
        _out.PlaybackStopped += (_, e) => { if (!_disposed) Finished?.Invoke(e.Exception); };
    }

    public bool IsPaused => _out.PlaybackState == PlaybackState.Paused;

    public void Play() => _out.Play();

    public void Pause()
    {
        if (_out.PlaybackState == PlaybackState.Playing) _out.Pause();
    }

    public void TogglePause()
    {
        if (_out.PlaybackState == PlaybackState.Playing) _out.Pause();
        else _out.Play();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }

    /// <summary>Pulls float samples from the buffer; silence while starved, end of stream only when the buffer is finished.</summary>
    private sealed class BufferSampleProvider(Core.SpeechPlaybackBuffer buffer) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Core.SpeechPlaybackBuffer.SampleRate, 1);

        public int Read(float[] samples, int offset, int count)
        {
            int n = buffer.Read(samples, offset, count);
            if (n == count) return n;
            // 0 ends WaveOut's playback: only when nothing more will come.
            if (n == 0 && buffer.IsFinished) return 0;
            Array.Clear(samples, offset + n, count - n);
            return count;
        }
    }
}
