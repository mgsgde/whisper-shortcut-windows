using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using WhisperShortcut.Core;

namespace WhisperShortcut;

/// <summary>
/// Captures a Live Meeting: the microphone (16 kHz mono 16-bit) and, when asked, what the PC plays (WASAPI loopback)
/// mixed into it. The mic is the clock: every mic buffer takes the same number of samples from a
/// <see cref="SystemAudioBuffer"/> that the loopback callback fills. If the loopback cannot start or the
/// default output changes, it is rebuilt (debounced, three retries). The microphone path is never stopped
/// for a system-audio failure, and a failed rebuild does not latch system audio off for the rest of the meeting.
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
    private int _loopbackToken;
    private volatile int _restarting;
    /// <summary>Which start or rebuild set <see cref="_restarting"/>. An older attempt must not clear a newer one.</summary>
    private int _restartGeneration;
    private volatile bool _loopbackDied;
    private volatile bool _systemActive;
    private volatile float _level;
    private bool _stopped;
    private int _rebuildGeneration;
    private SystemAudioRebuild.State _rebuild = SystemAudioRebuild.Idle;
    private DateTime _loopbackStartedAt = DateTime.MinValue;
    private string? _loopbackDeviceId;
    private MMDeviceEnumerator? _deviceEnumerator;
    private MeetingOutputNotificationClient? _deviceNotifications;

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
    /// loopback when requested. A loopback failure is logged and never thrown; a later output change retries it.
    /// </summary>
    public void Start()
    {
        _stopped = false;
        _rebuild = SystemAudioRebuild.Idle;
        _loopbackDied = false;
        _micStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // NAudio's WaveInEvent captures SynchronizationContext.Current in its constructor and posts
        // RecordingStopped to it. Stop() blocks the UI thread on _micStopped, so with the UI context the
        // handler could only run after the 2 s wait timed out. Create it without a context so the handler
        // runs on the capture thread.
        WaveInEvent waveIn;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            waveIn = new WaveInEvent
            {
                DeviceNumber = Microphones.DeviceNumber(_microphone),
                WaveFormat = new WaveFormat(TargetRate, 16, 1),
                BufferMilliseconds = 50,
            };
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
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

        // Notifications before the first loopback, so a device change during start is not missed.
        // The loopback goes first so the mixer already has system audio when the first mic buffer arrives.
        // _restarting makes a stop that lands during this start count as a failed start (retried below)
        // instead of a second rebuild scheduled from the callback. Generation 0 is this start; a rebuild
        // that begins during it owns a higher generation and must not have its flag cleared here.
        if (_mixSystemAudio)
        {
            StartNotifications();
            lock (_gate)
            {
                _restartGeneration = 0;
                _restarting = 1;
            }
            bool started;
            try { started = StartLoopback(null); }
            finally
            {
                lock (_gate)
                {
                    if (_restartGeneration == 0) _restarting = 0;
                }
            }
            if (!started || _loopbackDied) ScheduleInitialRetry();
        }

        try
        {
            waveIn.StartRecording();
        }
        catch (Exception e)
        {
            waveIn.Dispose();
            _waveIn = null;
            lock (_gate)
            {
                _stopped = true;
                _rebuildGeneration++;
            }
            StopNotifications();
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
            _rebuildGeneration++; // drops a rebuild that is waiting or about to start
        }
        StopNotifications();
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

    /// <summary>WASAPI thread. Render + Console or Multimedia only; Communications is a call device, not the loopback.</summary>
    internal void OnDefaultOutputChanged(DataFlow flow, Role role, string? deviceId)
    {
        try
        {
            if (flow != DataFlow.Render) return;
            if (role != Role.Console && role != Role.Multimedia) return;
            lock (_gate)
            {
                if (_stopped || !_mixSystemAudio) return;
                // Console and Multimedia usually move together. The one we already captured is not a new route.
                if (_systemActive && !string.IsNullOrEmpty(deviceId) && deviceId == _loopbackDeviceId) return;
                Log.Info("Meeting: default output device changed, scheduling PC audio rebuild");
                var (state, wait) = SystemAudioRebuild.DeviceChanged(_rebuild);
                _rebuild = state;
                Arm(wait);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Meeting: output device change ignored ({e.GetType().Name})");
        }
    }

    private void ScheduleInitialRetry()
    {
        lock (_gate)
        {
            if (_stopped || _rebuild.InFlight) return;
            Log.Warn("Meeting: PC audio capture failed at start, retrying");
            var (state, wait) = SystemAudioRebuild.AttemptFinished(new SystemAudioRebuild.State(0, true, false), false);
            _rebuild = state;
            Arm(wait);
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>. Bumps the generation so an older wait does not also rebuild.</summary>
    private void Arm(double? waitSeconds)
    {
        if (waitSeconds == null || _stopped) return;
        var generation = ++_rebuildGeneration;
        var wait = waitSeconds.Value;
        _ = Task.Run(() => AttemptAsync(wait, generation));
    }

    private async Task AttemptAsync(double waitSeconds, int generation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds)).ConfigureAwait(false);
            int attempt;
            lock (_gate)
            {
                if (_stopped || generation != _rebuildGeneration) return;
                _restartGeneration = generation;
                _restarting = 1;
                _loopbackDied = false;
                attempt = _rebuild.Failures + 1;
            }
            Log.Info($"Meeting: rebuilding PC audio capture (attempt {attempt})");
            var previous = ExchangeLoopback(null);
            DisposeLoopback(previous);
            _system.Clear();

            var started = false;
            try
            {
                started = StartLoopback(generation);
                if (started && _loopbackDied) started = false;
            }
            catch (Exception e)
            {
                Log.Warn($"Meeting: PC audio rebuild threw ({e.GetType().Name})");
                started = false;
            }
            finally
            {
                // A newer attempt may already own the flag. Clearing it here would hide that attempt's death.
                lock (_gate)
                {
                    if (_restartGeneration == generation) _restarting = 0;
                }
            }

            lock (_gate)
            {
                if (_stopped || generation != _rebuildGeneration) return;
                var ok = started && !_loopbackDied;
                var (state, next) = SystemAudioRebuild.AttemptFinished(_rebuild, ok);
                _rebuild = state;
                if (ok) Log.Info("Meeting: PC audio capture rebuilt");
                else if (next == null)
                    Log.Warn("Meeting: PC audio rebuild failed after 3 retries; the next output change will try again");
                Arm(next);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Meeting: PC audio rebuild failed ({e.GetType().Name})");
        }
    }

    private void StartNotifications()
    {
        MMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            var client = new MeetingOutputNotificationClient(this);
            enumerator.RegisterEndpointNotificationCallback(client);
            _deviceEnumerator = enumerator;
            _deviceNotifications = client;
            enumerator = null;
        }
        catch (Exception e)
        {
            Log.Warn($"Meeting: output device listener not started ({e.GetType().Name})");
            try { enumerator?.Dispose(); } catch { }
            _deviceEnumerator = null;
            _deviceNotifications = null;
        }
    }

    private void StopNotifications()
    {
        var enumerator = _deviceEnumerator;
        var client = _deviceNotifications;
        _deviceEnumerator = null;
        _deviceNotifications = null;
        if (enumerator == null) return;
        if (client != null)
        {
            try { enumerator.UnregisterEndpointNotificationCallback(client); }
            catch (Exception e) { Log.Warn($"Meeting: output device listener not unregistered ({e.GetType().Name})"); }
        }
        try { enumerator.Dispose(); } catch { }
    }

    /// <summary>
    /// Opens loopback on the current default render device. <paramref name="generation"/> is the rebuild
    /// this belongs to; null is the capture's own start. Returns false when it could not start — never throws.
    /// </summary>
    private bool StartLoopback(int? generation)
    {
        WasapiLoopbackCapture loopback;
        bool isFloat;
        int channels;
        StreamResampler resampler;
        WaveFormat format;
        WaveFormatEncoding encoding;
        string? deviceId = null;
        try
        {
            // One read: the capture and the id we compare on the next device change come from the same endpoint.
            // AudioClient activates its own COM object, so the device can be disposed once the capture exists.
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            deviceId = device.ID;
            loopback = new WasapiLoopbackCapture(device);
            format = loopback.WaveFormat;
            encoding = format is WaveFormatExtensible ext ? ext.ToStandardWaveFormat().Encoding : format.Encoding;
            if (encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
                isFloat = true;
            else if (encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
                isFloat = false;
            else
            {
                loopback.Dispose();
                Log.Warn($"Meeting: PC audio format {encoding} {format.BitsPerSample} bit not supported, microphone only");
                return false;
            }
            channels = Math.Max(1, format.Channels);
            resampler = new StreamResampler(format.SampleRate, TargetRate);
        }
        catch (Exception e)
        {
            Log.Warn($"Meeting: PC audio capture not available ({e.GetType().Name}), microphone only");
            return false;
        }

        var token = Volatile.Read(ref _loopbackToken);
        loopback.DataAvailable += OnLoopbackData;
        loopback.RecordingStopped += (_, e) => OnLoopbackStopped(token, e.Exception);

        var discarded = false;
        lock (_gate)
        {
            if (_stopped || (generation != null && generation.Value != _rebuildGeneration))
                discarded = true;
            else
            {
                _loopbackChannels = channels;
                _loopbackFloat = isFloat;
                _resampler = resampler;
                _loopback = loopback;
                _loopbackDeviceId = deviceId;
                try
                {
                    loopback.StartRecording();
                    // A stop that arrived during StartRecording is a failed start, not a live capture.
                    if (_loopbackDied)
                    {
                        discarded = true;
                    }
                    else
                    {
                        _systemActive = true;
                        _loopbackStartedAt = DateTime.UtcNow;
                    }
                }
                catch (Exception e)
                {
                    Log.Warn($"Meeting: PC audio capture not available ({e.GetType().Name}), microphone only");
                    discarded = true;
                }
                if (discarded)
                {
                    _loopback = null;
                    _resampler = null;
                    _systemActive = false;
                    _loopbackDeviceId = null;
                    Interlocked.Increment(ref _loopbackToken);
                }
            }
        }
        if (discarded)
        {
            // The handler was subscribed with the token from before this bump, so dispose cannot
            // schedule another rebuild for a capture we are throwing away.
            Interlocked.Increment(ref _loopbackToken);
            DisposeLoopback(loopback);
            return false;
        }

        Log.Info($"Meeting: PC audio capture started ({format.SampleRate} Hz, {format.Channels} ch, {encoding})");
        return true;
    }

    private void OnLoopbackStopped(int token, Exception? error)
    {
        try
        {
            if (error == null || token != Volatile.Read(ref _loopbackToken)) return;
            // Before the _restarting check. AttemptAsync clears that flag in finally, then looks at
            // _loopbackDied under the lock: a death in that window has to be visible or the attempt
            // is counted as a success and never rebuilt.
            _loopbackDied = true;
            _systemActive = false;
            if (_restarting != 0) return;
            lock (_gate)
            {
                if (_stopped || token != Volatile.Read(ref _loopbackToken)) return;
                var age = _loopbackStartedAt == DateTime.MinValue
                    ? 0
                    : (DateTime.UtcNow - _loopbackStartedAt).TotalSeconds;
                var (state, wait) = SystemAudioRebuild.CaptureStopped(_rebuild, age);
                if (wait == null && state.Equals(_rebuild)) return;
                _rebuild = state;
                if (wait == null)
                    Log.Warn("Meeting: PC audio stopped and the rebuild budget is used; the next output change will try again");
                else
                    Log.Warn($"Meeting: PC audio stopped ({error.GetType().Name}), rebuilding");
                Arm(wait);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Meeting: PC audio stop handler failed ({e.GetType().Name})");
        }
    }

    private void StopLoopback() => DisposeLoopback(ExchangeLoopback(null));

    /// <summary>Swaps the published loopback for <paramref name="next"/> and invalidates its stop handler.</summary>
    private WasapiLoopbackCapture? ExchangeLoopback(WasapiLoopbackCapture? next)
    {
        lock (_gate)
        {
            var previous = _loopback;
            _loopback = next;
            if (next == null)
            {
                _resampler = null;
                _systemActive = false;
            }
            Interlocked.Increment(ref _loopbackToken);
            return previous;
        }
    }

    private static void DisposeLoopback(WasapiLoopbackCapture? loopback)
    {
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

/// <summary>
/// WASAPI default-device notifications for one meeting capture. Public so COM can call it.
/// Callbacks arrive on a WASAPI thread and must not throw.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class MeetingOutputNotificationClient : IMMNotificationClient
{
    private readonly MeetingAudioCapture _owner;

    internal MeetingOutputNotificationClient(MeetingAudioCapture owner) => _owner = owner;

    /// <inheritdoc/>
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }

    /// <inheritdoc/>
    public void OnDeviceAdded(string pwstrDeviceId) { }

    /// <inheritdoc/>
    public void OnDeviceRemoved(string deviceId) { }

    /// <inheritdoc/>
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // IMMNotificationClient runs on the MMDevice thread and must not wait on our locks.
        try
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                _ => _owner.OnDefaultOutputChanged(flow, role, defaultDeviceId), null);
        }
        catch
        {
            // Never throw back into COM.
        }
    }

    /// <inheritdoc/>
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}
