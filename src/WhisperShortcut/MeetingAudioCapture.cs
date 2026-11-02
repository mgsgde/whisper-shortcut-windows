using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WhisperShortcut.Core;

namespace WhisperShortcut;

/// <summary>
/// Captures a Live Meeting: the microphone (16 kHz mono 16-bit) and, when asked, what the PC plays (WASAPI loopback)
/// mixed into it. The mic is the clock: every mic buffer takes the same number of samples from a
/// <see cref="SystemAudioBuffer"/> that the loopback callback fills. If the loopback cannot start or breaks,
/// the meeting goes on with the microphone alone.
/// </summary>
internal sealed class MeetingAudioCapture : IDisposable
{
    private const int TargetRate = 16000;
    private const int NoEndpointHResult = unchecked((int)0x80070490); // E_NOTFOUND: no audio endpoint

    private readonly string? _microphone;
    private readonly bool _mixSystemAudio;
    private readonly Action<short[]> _onSamples;
    private readonly SystemAudioBuffer _system = new(TargetRate);
    private readonly object _gate = new();

    private WaveInEvent? _waveIn;
    private TaskCompletionSource? _micStopped;
    private WasapiLoopbackCapture? _loopback;
    private StreamResampler? _resampler;
    private int _loopbackChannels;
    private bool _loopbackFloat;
    private volatile bool _systemActive;
    private volatile float _level;
    private bool _stopped;

    /// <summary>
    /// <paramref name="microphone"/> is the stored device name (null or "" = Windows default).
    /// <paramref name="onSamples"/> gets 16 kHz mono 16-bit audio (with system audio mixed in when
    /// <paramref name="mixSystemAudio"/>) on the mic's capture thread, about every 50 ms.
    /// </summary>
    public MeetingAudioCapture(string? microphone, bool mixSystemAudio, Action<short[]> onSamples)
    {
        _microphone = microphone;
        _mixSystemAudio = mixSystemAudio;
        _onSamples = onSamples;
    }

    /// <summary>The loopback really started and is still running.</summary>
    public bool SystemAudioActive => _systemActive;

    /// <summary>Peak of the last mixed buffer, 0..1. Read from the UI thread, hence volatile.</summary>
    public float Level => _level;

    /// <summary>Raised (on a background thread) when the microphone stops with a device error.</summary>
    public event Action? DeviceLost;

    /// <summary>
    /// Starts the microphone (throws <see cref="MicrophoneUnavailableException"/> if it cannot be opened), then the
    /// loopback when requested. A loopback failure is logged and never thrown.
    /// </summary>
    public void Start()
    {
        _stopped = false;
        _micStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waveIn = new WaveInEvent
        {
            DeviceNumber = Microphones.DeviceNumber(_microphone),
            WaveFormat = new WaveFormat(TargetRate, 16, 1),
            BufferMilliseconds = 50,
        };
        waveIn.DataAvailable += OnMicData;
        waveIn.RecordingStopped += (_, e) =>
        {
            _micStopped?.TrySetResult();
            if (e.Exception != null && !_stopped)
            {
                Log.Error($"Meeting: microphone stopped by device error ({e.Exception.GetType().Name})");
                DeviceLost?.Invoke();
            }
        };
        _waveIn = waveIn;

        // The loopback goes first so the mixer already has system audio when the first mic buffer arrives.
        if (_mixSystemAudio) StartLoopback();

        try
        {
            waveIn.StartRecording();
        }
        catch (Exception e)
        {
            waveIn.Dispose();
            _waveIn = null;
            StopLoopback();
            throw new MicrophoneUnavailableException(e);
        }
    }

    /// <summary>Stops both captures; waits up to 2 s for the microphone to finish. Safe to call more than once.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
        }
        StopLoopback();
        var waveIn = _waveIn;
        _waveIn = null;
        if (waveIn != null)
        {
            try
            {
                waveIn.StopRecording();
                _micStopped?.Task.Wait(2000);
            }
            catch (Exception e)
            {
                Log.Warn($"Meeting: stopping the microphone failed ({e.GetType().Name})");
            }
            waveIn.Dispose();
        }
        _level = 0;
        _system.Clear();
    }

    /// <summary>Stops the capture.</summary>
    public void Dispose() => Stop();

    /// <summary>
    /// The default output device for <see cref="MeetingAudioRouting"/>: whether there is one, its form factor
    /// (null when unknown) and friendly name. No endpoint gives (false, null, null); another failure gives (true, null, null).
    /// </summary>
    public static (bool HasDevice, int? FormFactor, string? Name) DefaultOutput()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string? name = null;
            int? formFactor = null;
            try { name = device.FriendlyName; }
            catch (Exception e) { Log.Warn($"Meeting: output device name unreadable ({e.GetType().Name})"); }
            try
            {
                var value = device.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value;
                formFactor = value switch
                {
                    uint u => (int)u,
                    int i => i,
                    _ => null,
                };
            }
            catch (Exception e)
            {
                Log.Warn($"Meeting: output form factor unreadable ({e.GetType().Name})");
            }
            return (true, formFactor, name);
        }
        catch (COMException e) when (e.HResult == NoEndpointHResult)
        {
            return (false, null, null);
        }
        catch (Exception e)
        {
            Log.Error("Meeting: output device query failed", e);
            return (true, null, null);
        }
    }

    private void StartLoopback()
    {
        try
        {
            var loopback = new WasapiLoopbackCapture();
            var format = loopback.WaveFormat;
            var encoding = format is WaveFormatExtensible ext ? ext.ToStandardWaveFormat().Encoding : format.Encoding;
            if (encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
                _loopbackFloat = true;
            else if (encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
                _loopbackFloat = false;
            else
            {
                loopback.Dispose();
                Log.Warn($"Meeting: PC audio format {encoding} {format.BitsPerSample} bit not supported, microphone only");
                return;
            }
            _loopbackChannels = Math.Max(1, format.Channels);
            _resampler = new StreamResampler(format.SampleRate, TargetRate);
            loopback.DataAvailable += OnLoopbackData;
            loopback.RecordingStopped += (_, e) =>
            {
                _systemActive = false;
                if (e.Exception != null)
                    Log.Warn($"Meeting: PC audio stopped ({e.Exception.GetType().Name}), microphone only from here");
            };
            _loopback = loopback;
            loopback.StartRecording();
            _systemActive = true;
            Log.Info($"Meeting: PC audio capture started ({format.SampleRate} Hz, {format.Channels} ch, {encoding})");
        }
        catch (Exception e)
        {
            Log.Warn($"Meeting: PC audio capture not available ({e.GetType().Name}), microphone only");
            _systemActive = false;
            try { _loopback?.Dispose(); } catch { }
            _loopback = null;
        }
    }

    private void StopLoopback()
    {
        var loopback = _loopback;
        _loopback = null;
        _systemActive = false;
        if (loopback == null) return;
        try { loopback.StopRecording(); } catch { }
        try { loopback.Dispose(); } catch { }
    }

    private void OnLoopbackData(object? sender, WaveInEventArgs e)
    {
        var resampler = _resampler;
        if (resampler == null || e.BytesRecorded == 0) return;
        try
        {
            var bytes = e.Buffer.AsSpan(0, e.BytesRecorded);
            var interleaved = _loopbackFloat ? MeetingAudioMixer.Float32ToFloat(bytes) : MeetingAudioMixer.Pcm16ToFloat(bytes);
            var mono = MeetingAudioMixer.ToMono(interleaved, _loopbackChannels);
            _system.Push(resampler.Process(mono));
        }
        catch (Exception ex)
        {
            Log.Warn($"Meeting: PC audio buffer dropped ({ex.GetType().Name})");
        }
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        var count = e.BytesRecorded / 2;
        if (count == 0) return;
        var samples = new short[count];
        Buffer.BlockCopy(e.Buffer, 0, samples, 0, count * 2);

        if (_systemActive)
        {
            var system = new float[count];
            _system.Take(system);
            MeetingAudioMixer.MixInto(samples, system);
        }

        var peak = 0;
        foreach (var s in samples)
        {
            var a = Math.Abs((int)s);
            if (a > peak) peak = a;
        }
        _level = peak / 32768f;
        _onSamples(samples);
    }
}
