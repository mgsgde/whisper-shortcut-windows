using NAudio.Wave;

namespace WhisperShortcut;

/// <summary>No usable microphone.</summary>
internal sealed class MicrophoneUnavailableException : Exception
{
    public MicrophoneUnavailableException(Exception inner)
        : base("Kein Mikrofon gefunden. Bitte ein Mikrofon anschließen und in Windows als Standardgerät wählen.", inner) { }
}

/// <summary>
/// Records the system default microphone (WAVE_MAPPER, DeviceNumber -1, not device 0) as
/// 16 kHz mono 16-bit into an in-memory WAV. Stops itself at <see cref="MaxDuration"/>.
/// </summary>
internal sealed class MicAudioSource : Core.IAudioSource
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);

    /// <summary>Raised (on a background thread) when the recording hits the 5-minute limit.</summary>
    public event Action? MaxDurationReached;

    private readonly WaveFormat _format = new(16000, 16, 1);
    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;
    private TaskCompletionSource? _stopped;
    private int _peak;
    private bool _limitHit;

    public void Start()
    {
        _buffer = new MemoryStream();
        _writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(_buffer), _format);
        _peak = 0;
        _limitHit = false;
        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waveIn = new WaveInEvent { DeviceNumber = -1, WaveFormat = _format, BufferMilliseconds = 50 };
        _waveIn.DataAvailable += OnData;
        _waveIn.RecordingStopped += (_, _) => _stopped.TrySetResult();
        try
        {
            _waveIn.StartRecording();
        }
        catch (Exception e)
        {
            _waveIn.Dispose();
            _waveIn = null;
            throw new MicrophoneUnavailableException(e);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var writer = _writer;
        if (writer == null || _limitHit) return;
        writer.Write(e.Buffer, 0, e.BytesRecorded);
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            int sample = Math.Abs((int)BitConverter.ToInt16(e.Buffer, i));
            if (sample > _peak) _peak = sample;
        }
        if (writer.TotalTime >= MaxDuration)
        {
            _limitHit = true;
            MaxDurationReached?.Invoke();
        }
    }

    public async Task<byte[]> StopAsync()
    {
        if (_waveIn == null || _writer == null || _buffer == null) return Array.Empty<byte>();
        _waveIn.StopRecording();
        await Task.WhenAny(_stopped!.Task, Task.Delay(2000));
        _waveIn.Dispose();
        _waveIn = null;
        _writer.Dispose(); // writes the RIFF sizes; the stream stays open
        _writer = null;
        var bytes = _buffer.ToArray();
        _buffer = null;
        Core.Log.Info($"Mic: {bytes.Length} bytes, peak {_peak * 100 / 32768} %");
        // Peak below 1 %: not a quiet room, a blocked device.
        if (_peak < 328) throw new Core.AudioSilentException();
        return bytes;
    }

    public void Cancel()
    {
        try { _waveIn?.StopRecording(); } catch { }
        _waveIn?.Dispose();
        _waveIn = null;
        _writer?.Dispose();
        _writer = null;
        _buffer = null;
    }
}
