using NAudio.Wave;

namespace WhisperShortcut;

/// <summary>No usable microphone.</summary>
internal sealed class MicrophoneUnavailableException : Exception
{
    public MicrophoneUnavailableException(Exception inner)
        : base("Kein Mikrofon gefunden oder Zugriff gesperrt. Bitte ein Mikrofon anschließen und in Windows als Standardgerät wählen. Zugriff prüfen: Einstellungen → Datenschutz (und Sicherheit) → Mikrofon → Desktop-Apps den Zugriff erlauben.", inner) { }
}

/// <summary>
/// Records the chosen microphone (see <see cref="Microphones.DeviceNumber"/>; empty = the Windows
/// default, WAVE_MAPPER -1, not device 0) as 16 kHz mono 16-bit into an in-memory WAV. Stops itself at <see cref="MaxDuration"/>.
/// </summary>
internal sealed class MicAudioSource : Core.IAudioSource
{
    /// <summary>Dictation can run this long; the recording is split into chunks before it is sent.</summary>
    public static readonly TimeSpan DictateLimit = TimeSpan.FromMinutes(30);

    /// <summary>Dictate Prompt sends the audio inline in one request, so it stays at 5 minutes.</summary>
    public static readonly TimeSpan PromptLimit = TimeSpan.FromMinutes(5);

    /// <summary>Raised (on a background thread) when the recording hits <see cref="MaxDuration"/>.</summary>
    public event Action? MaxDurationReached;

    /// <summary>Raised (on a background thread) when the device vanishes mid-recording (e.g. USB headset unplugged).</summary>
    public event Action? DeviceLost;

    private readonly WaveFormat _format = new(16000, 16, 1);
    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;
    private TaskCompletionSource? _stopped;
    private int _peak;
    private volatile float _level;
    private bool _limitHit;

    private readonly string? _microphone;

    /// <summary>
    /// Records until <paramref name="maxDuration"/>, then raises <see cref="MaxDurationReached"/>.
    /// <paramref name="microphone"/> is the stored device name; null or "" = Windows default.
    /// </summary>
    public MicAudioSource(TimeSpan maxDuration, string? microphone = null)
    {
        MaxDuration = maxDuration;
        _microphone = microphone;
    }

    /// <summary>Stops the recording on its own once this time is reached.</summary>
    public TimeSpan MaxDuration { get; }

    /// <summary>
    /// Peak of the most recent capture buffer, 0..1. The overlay timer reads it every 50 ms
    /// from the UI thread while capture writes it, so the backing field is volatile.
    /// </summary>
    public float Level => _level;

    /// <summary>Loudest sample since <see cref="Start"/>, as 0..1. The overlay uses it to notice a blocked microphone.</summary>
    public float PeakSoFar => Volatile.Read(ref _peak) / 32768f;

    public void Start()
    {
        _buffer = new MemoryStream();
        _writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(_buffer), _format);
        // Level is read from the UI thread; publish the reset so the meter does not show the previous take.
        Volatile.Write(ref _peak, 0);
        _level = 0;
        _limitHit = false;
        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waveIn = new WaveInEvent { DeviceNumber = Microphones.DeviceNumber(_microphone), WaveFormat = _format, BufferMilliseconds = 50 };
        _waveIn.DataAvailable += OnData;
        _waveIn.RecordingStopped += (_, e) =>
        {
            _stopped.TrySetResult();
            if (e.Exception != null)
            {
                Core.Log.Error($"Mic: recording stopped by device error ({e.Exception.GetType().Name})");
                DeviceLost?.Invoke();
            }
        };
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
        int bufferPeak = 0;
        int peak = _peak;
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            int sample = Math.Abs((int)BitConverter.ToInt16(e.Buffer, i));
            if (sample > bufferPeak) bufferPeak = sample;
            if (sample > peak) peak = sample;
        }
        if (peak != _peak) Volatile.Write(ref _peak, peak);
        _level = bufferPeak / 32768f;
        if (writer.TotalTime >= MaxDuration)
        {
            _limitHit = true;
            MaxDurationReached?.Invoke();
        }
    }

    public async Task<byte[]> StopAsync()
    {
        _level = 0;
        if (_waveIn == null || _writer == null || _buffer == null) return Array.Empty<byte>();
        _waveIn.StopRecording();
        await Task.WhenAny(_stopped!.Task, Task.Delay(2000));
        _waveIn.Dispose();
        _waveIn = null;
        _writer.Dispose(); // writes the RIFF sizes; the stream stays open
        _writer = null;
        var bytes = _buffer.ToArray();
        _buffer = null;
        var peak = Volatile.Read(ref _peak);
        Core.Log.Info($"Mic: {bytes.Length} bytes, peak {peak * 100 / 32768} %");
        // Only digital silence (peak below ~-60 dBFS, what a privacy-blocked device delivers) is a device
        // problem. A quiet room is judged by the speech gate, which says "Nichts erkannt" instead.
        // Ultra-short recordings (< 0.5 s = 16000 bytes at 16 kHz mono 16-bit) are not judged.
        if (peak < 33 && bytes.Length > 44 + 16000) throw new Core.AudioSilentException();
        _level = 0;
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
        _level = 0;
    }
}
