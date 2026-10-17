using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;

namespace WhisperShortcut;

/// <summary>Tray icons drawn at runtime, one per state, so no .ico files are needed.</summary>
internal static class TrayIcons
{
    public static readonly Icon Ready = Draw(Color.FromArgb(52, 64, 84), Glyph.Mic);
    public static readonly Icon Recording = Draw(Color.FromArgb(220, 38, 38), Glyph.Dot);
    public static readonly Icon Processing = Draw(Color.FromArgb(217, 119, 6), Glyph.Dots);

    private enum Glyph { Mic, Dot, Dots }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon Draw(Color background, Glyph glyph)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(background);
            g.FillEllipse(bg, 0, 0, size - 1, size - 1);
            using var fg = new SolidBrush(Color.White);
            float u = size / 16f;
            switch (glyph)
            {
                case Glyph.Mic:
                    using (var pen = new Pen(Color.White, 1.3f * u))
                    {
                        g.FillRectangle(fg, 6.3f * u, 3f * u, 3.4f * u, 6.5f * u);
                        g.FillEllipse(fg, 6.3f * u, 2f * u, 3.4f * u, 2.6f * u);
                        g.FillEllipse(fg, 6.3f * u, 8f * u, 3.4f * u, 2.6f * u);
                        g.DrawArc(pen, 4.4f * u, 5f * u, 7.2f * u, 6.6f * u, 0, 180);
                        g.DrawLine(pen, 8f * u, 11.6f * u, 8f * u, 13.4f * u);
                    }
                    break;
                case Glyph.Dot:
                    g.FillEllipse(fg, 5f * u, 5f * u, 6f * u, 6f * u);
                    break;
                case Glyph.Dots:
                    for (int i = 0; i < 3; i++) g.FillEllipse(fg, (3.2f + i * 3.6f) * u, 7f * u, 2.4f * u, 2.4f * u);
                    break;
            }
        }
        IntPtr handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
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
