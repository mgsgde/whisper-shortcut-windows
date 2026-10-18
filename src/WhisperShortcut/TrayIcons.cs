using System.Media;

namespace WhisperShortcut;

/// <summary>
/// App and tray icons, embedded from the .ico files that tools/make_icon.py builds from the macOS
/// app icon. Ready is the app icon; Recording (red, dot) and Processing (amber, dots) keep its shape.
/// </summary>
internal static class TrayIcons
{
    /// <summary>All sizes, for windows (Windows picks the title-bar and taskbar size itself).</summary>
    public static readonly Icon App = Load("app.ico", null);
    public static readonly Icon Ready = Load("app.ico", SystemInformation.SmallIconSize);
    public static readonly Icon Recording = Load("recording.ico", SystemInformation.SmallIconSize);
    public static readonly Icon Processing = Load("processing.ico", SystemInformation.SmallIconSize);

    private static Icon Load(string name, Size? size)
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("WhisperShortcut." + name)
            ?? throw new InvalidOperationException($"Embedded icon {name} missing");
        return size is { } s ? new Icon(stream, s) : new Icon(stream);
    }
}

/// <summary>Short sine tones for start, stop and error (generated, not system sounds).</summary>
internal static class Sounds
{
    private static readonly byte[] StartTone = Tone(880, 70);
    private static readonly byte[] StopTone = Tone(660, 70);
    private static readonly byte[] ErrorTone = Tone(300, 180);

    public static void Start() => Play(StartTone);
    public static void Stop() => Play(StopTone);
    public static void Error() => Play(ErrorTone);

    private static void Play(byte[] wav)
    {
        try
        {
            var player = new SoundPlayer(new MemoryStream(wav));
            player.Play(); // async, own thread
        }
        catch (Exception e)
        {
            Core.Log.Error("Sound failed", e);
        }
    }

    private static byte[] Tone(int hz, int ms)
    {
        const int rate = 22050;
        int samples = rate * ms / 1000;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        for (int i = 0; i < samples; i++)
        {
            // 8 ms fade in/out so the tone does not click.
            double fade = Math.Min(1, Math.Min(i, samples - i) / (rate * 0.008));
            w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 9000 * fade));
        }
        w.Flush();
        return stream.ToArray();
    }
}
