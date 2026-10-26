using NAudio.Wave;

namespace WhisperShortcut;

/// <summary>Lists recording devices and maps the stored device name to a WinMM device number.</summary>
internal static class Microphones
{
    /// <summary>Product names of all recording devices, in device order. Empty if the query fails.</summary>
    public static IReadOnlyList<string> Names()
    {
        try
        {
            var names = new List<string>();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                names.Add(WaveInEvent.GetCapabilities(i).ProductName);
            return names;
        }
        catch (Exception e)
        {
            Core.Log.Error("Mic: device list failed", e);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// WinMM device number for a stored name. "" or null = -1 (WAVE_MAPPER, the Windows default,
    /// not device 0). A name that is no longer present falls back to -1.
    /// </summary>
    public static int DeviceNumber(string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        try
        {
            // WinMM truncates product names to 31 chars, so the stored value is that same truncated
            // name (it came from Names()) and compares equal here.
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                if (string.Equals(WaveInEvent.GetCapabilities(i).ProductName, name, StringComparison.Ordinal))
                    return i;
        }
        catch (Exception e)
        {
            Core.Log.Error("Mic: device lookup failed", e);
        }
        Core.Log.Info($"Mic: \"{name}\" not found, using the Windows default");
        return -1;
    }
}

/// <summary>
/// Live input level for the "Mikrofon testen" meter. Keeps only the peak of the latest buffer,
/// nothing is recorded to memory.
/// </summary>
internal sealed class LevelProbe : IDisposable
{
    private readonly string? _microphone;
    private WaveInEvent? _waveIn;
    private volatile float _level;

    /// <summary>Probes the device stored under <paramref name="microphone"/> ("" = Windows default).</summary>
    public LevelProbe(string? microphone) => _microphone = microphone;

    /// <summary>Peak of the latest 50 ms buffer, 0..1. Read from the UI thread while capture writes it, hence volatile.</summary>
    public float Level => _level;

    /// <summary>Starts capturing; throws <see cref="MicrophoneUnavailableException"/> if the device cannot be opened.</summary>
    public void Start()
    {
        if (_waveIn != null) return;
        var waveIn = new WaveInEvent
        {
            DeviceNumber = Microphones.DeviceNumber(_microphone),
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50,
        };
        waveIn.DataAvailable += (_, e) =>
        {
            int peak = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                int sample = Math.Abs((int)BitConverter.ToInt16(e.Buffer, i));
                if (sample > peak) peak = sample;
            }
            _level = peak / 32768f;
        };
        try
        {
            waveIn.StartRecording();
        }
        catch (Exception e)
        {
            waveIn.Dispose();
            throw new MicrophoneUnavailableException(e);
        }
        _waveIn = waveIn;
    }

    /// <summary>Stops and releases the device. Safe to call more than once.</summary>
    public void Dispose()
    {
        var waveIn = _waveIn;
        _waveIn = null;
        _level = 0;
        if (waveIn == null) return;
        try { waveIn.StopRecording(); } catch { }
        waveIn.Dispose();
    }
}
