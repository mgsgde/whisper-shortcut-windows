using System.Runtime.InteropServices;
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
/// When a checkpoint mode is set, PCM is also spilled to
/// <see cref="Core.RecordingCheckpoint"/> so a crash does not lose the take.
/// </summary>
internal sealed class MicAudioSource : Core.IAudioSource
{
    /// <summary>Dictation can run this long; the recording is split into chunks before it is sent.</summary>
    public static readonly TimeSpan DictateLimit = TimeSpan.FromMinutes(30);

    /// <summary>Dictate Prompt sends the audio inline in one request, so it stays at 5 minutes.</summary>
    public static readonly TimeSpan PromptLimit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long capture keeps running after Stop, so the last word is not cut off
    /// (Mac: MenuBarController.audioTailCaptureDelay).
    /// </summary>
    public static readonly TimeSpan TailCapture = TimeSpan.FromMilliseconds(200);

    /// <summary>Raised (on a background thread) when the recording hits <see cref="MaxDuration"/>.</summary>
    public event Action? MaxDurationReached;

    /// <summary>Raised (on a background thread) when the device vanishes mid-recording (e.g. USB headset unplugged, sleep).</summary>
    public event Action? DeviceLost;

    /// <summary>Raised once when the crash-spill file can no longer be written (usually a full disk).</summary>
    public event Action? CheckpointFailed;

    private readonly WaveFormat _format = new(16000, 16, 1);
    private readonly object _gate = new();
    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;
    private TaskCompletionSource? _stopped;
    private int _peak;
    private volatile float _level;
    private bool _limitHit;
    private int _userStopping;
    private int _lost;
    private long _lastBufferMs;
    private System.Threading.Timer? _watch;
    private Core.RecordingCheckpoint? _checkpoint;
    private int _checkpointNotified;

    private readonly string? _microphone;
    private readonly string? _checkpointMode;

    /// <summary>
    /// Records until <paramref name="maxDuration"/>, then raises <see cref="MaxDurationReached"/>.
    /// <paramref name="microphone"/> is the stored device name; null or "" = Windows default.
    /// <paramref name="checkpointMode"/> is "Dictate" or "Prompt" when the take should survive a crash.
    /// </summary>
    public MicAudioSource(TimeSpan maxDuration, string? microphone = null, string? checkpointMode = null)
    {
        MaxDuration = maxDuration;
        _microphone = microphone;
        _checkpointMode = checkpointMode;
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
        // 90 s at 16 kHz mono: typical dictations fit without MemoryStream doubling copies.
        _buffer = new MemoryStream(16_000 * 2 * 90 + 64);
        _writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(_buffer), _format);
        // Level is read from the UI thread; publish the reset so the meter does not show the previous take.
        Volatile.Write(ref _peak, 0);
        _level = 0;
        _limitHit = false;
        _userStopping = 0;
        _lost = 0;
        _checkpointNotified = 0;
        _lastBufferMs = Environment.TickCount64;
        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!string.IsNullOrEmpty(_checkpointMode))
            _checkpoint = Core.RecordingCheckpoint.TryBegin(Core.RecordingCheckpoint.DefaultDirectory, _checkpointMode);
        _watch = new System.Threading.Timer(_ => CheckStall(), null, 1000, 1000);
        // 20 ms: Stop waits for the last buffer, so this is ~30 ms less tail than 50 ms and is still safe for WinMM.
        _waveIn = new WaveInEvent { DeviceNumber = Microphones.DeviceNumber(_microphone), WaveFormat = _format, BufferMilliseconds = 20 };
        _waveIn.DataAvailable += OnData;
        _waveIn.RecordingStopped += (_, e) =>
        {
            _stopped.TrySetResult();
            if (e.Exception != null)
            {
                Core.Log.Error($"Mic: recording stopped by device error ({e.Exception.GetType().Name})");
                RaiseLost();
            }
            else if (Volatile.Read(ref _userStopping) == 0)
            {
                Core.Log.Error("Mic: recording stopped unexpectedly (device change or sleep)");
                RaiseLost();
            }
        };
        try
        {
            _waveIn.StartRecording();
        }
        catch (Exception e)
        {
            StopWatch();
            _checkpoint?.Discard();
            _checkpoint = null;
            _waveIn.Dispose();
            _waveIn = null;
            _writer?.Dispose();
            _writer = null;
            _buffer = null;
            throw new MicrophoneUnavailableException(e);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        Volatile.Write(ref _lastBufferMs, Environment.TickCount64);
        bool limit = false;
        bool checkpointFailed = false;
        lock (_gate)
        {
            if (_writer == null || _limitHit) return;
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            if (_checkpoint != null && !_checkpoint.TryAppend(e.Buffer.AsSpan(0, e.BytesRecorded))
                && Interlocked.Exchange(ref _checkpointNotified, 1) == 0)
                checkpointFailed = true;
            if (_writer.TotalTime >= MaxDuration)
            {
                _limitHit = true;
                limit = true;
            }
        }
        if (checkpointFailed) CheckpointFailed?.Invoke();
        int bufferPeak = 0;
        int peak = _peak;
        var samples = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded & ~1));
        foreach (var s in samples)
        {
            int sample = Math.Abs((int)s);
            if (sample > bufferPeak) bufferPeak = sample;
            if (sample > peak) peak = sample;
        }
        if (peak != _peak) Volatile.Write(ref _peak, peak);
        _level = bufferPeak / 32768f;
        if (limit) MaxDurationReached?.Invoke();
    }

    private void CheckStall()
    {
        if (Volatile.Read(ref _userStopping) != 0 || _limitHit || _waveIn == null) return;
        if (Core.CaptureWatchdog.IsStalled(Volatile.Read(ref _lastBufferMs), Environment.TickCount64))
        {
            Core.Log.Error("Mic: no audio buffers for 3 s (device lost, sleep or driver stop)");
            RaiseLost();
        }
    }

    private void RaiseLost()
    {
        if (Interlocked.Exchange(ref _lost, 1) != 0) return;
        DeviceLost?.Invoke();
    }

    private void StopWatch()
    {
        _watch?.Dispose();
        _watch = null;
    }

    public async Task<byte[]> StopAsync()
    {
        Volatile.Write(ref _userStopping, 1);
        StopWatch();
        _level = 0;
        WaveInEvent? waveIn;
        lock (_gate)
        {
            if (_waveIn == null || _writer == null || _buffer == null) return Array.Empty<byte>();
            waveIn = _waveIn;
        }
        // Keep the device open briefly so the last syllable after the hotkey is still captured.
        // A max-duration stop already has everything; waiting would only add silence.
        if (!_limitHit) await Task.Delay(TailCapture);
        try { waveIn.StopRecording(); } catch { /* already stopped after a device loss */ }
        await Task.WhenAny(_stopped!.Task, Task.Delay(2000));

        WaveFileWriter? writer;
        MemoryStream? buffer;
        Core.RecordingCheckpoint? checkpoint;
        lock (_gate)
        {
            // Drop the writer first so a late DataAvailable cannot Write after Dispose.
            waveIn = _waveIn;
            writer = _writer;
            buffer = _buffer;
            checkpoint = _checkpoint;
            _waveIn = null;
            _writer = null;
            _buffer = null;
            _checkpoint = null;
        }
        // WaveIn.Dispose may wait for the capture callback; do not hold _gate across it.
        waveIn?.Dispose();
        writer?.Dispose(); // writes the RIFF sizes; the stream stays open
        var bytes = buffer?.ToArray() ?? Array.Empty<byte>();
        checkpoint?.Dispose(); // keep the file until processing succeeds
        var peak = Volatile.Read(ref _peak);
        Core.Log.Info($"Mic: {bytes.Length} bytes, peak {peak * 100 / 32768} %");
        // Only digital silence (peak below ~-60 dBFS, what a privacy-blocked device delivers) is a device
        // problem. A quiet room is judged by the speech gate, which says "Nichts erkannt" instead.
        // Ultra-short recordings (< 0.5 s = 16000 bytes at 16 kHz mono 16-bit) are not judged.
        if (peak < 33 && bytes.Length > 44 + 16000) throw new Core.AudioSilentException();
        _level = 0;
        return bytes;
    }

    /// <summary>
    /// Stops capture without returning audio. <paramref name="discardCheckpoint"/> deletes the crash
    /// spill (Esc). Leave it when the process is quitting, so the next start can recover.
    /// </summary>
    public void Cancel(bool discardCheckpoint = true)
    {
        Volatile.Write(ref _userStopping, 1);
        StopWatch();
        WaveInEvent? waveIn;
        WaveFileWriter? writer;
        Core.RecordingCheckpoint? checkpoint;
        lock (_gate)
        {
            waveIn = _waveIn;
            writer = _writer;
            checkpoint = _checkpoint;
            _waveIn = null;
            _writer = null;
            _buffer = null;
            _checkpoint = null;
            _level = 0;
        }
        try { waveIn?.StopRecording(); } catch { }
        waveIn?.Dispose();
        writer?.Dispose();
        if (discardCheckpoint) checkpoint?.Discard();
        else checkpoint?.Dispose();
    }
}
