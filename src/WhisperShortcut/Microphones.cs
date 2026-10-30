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
            {
                // WinMM cuts names at 31 chars, so two devices can share one. DeviceNumber() finds only
                // the first, so list each name once: no entry the lookup could not honor.
                var name = WaveInEvent.GetCapabilities(i).ProductName;
                if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }
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
    /// not device 0). A re-enumerated USB name ("2- " inside the parentheses) still resolves when
    /// it is the only match. A name that is no longer present falls back to -1.
    /// </summary>
    public static int DeviceNumber(string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        try
        {
            // WinMM keeps 31 characters, and a re-enumerated USB endpoint inserts "N- " after
            // the opening parenthesis. MicMatch.Find accepts the saved name in either form.
            var names = new List<string>();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                names.Add(WaveInEvent.GetCapabilities(i).ProductName);
            int index = Core.MicMatch.Find(name, names);
            if (index >= 0)
            {
                if (!string.Equals(names[index], name, StringComparison.Ordinal))
                    Core.Log.Info($"Mic: \"{name}\" now appears as \"{names[index]}\"");
                return index;
            }
            Core.Log.Info($"Mic: \"{name}\" not found among {string.Join(" | ", names)}, using the Windows default");
        }
        catch (Exception e)
        {
            Core.Log.Error("Mic: device lookup failed", e);
        }
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
