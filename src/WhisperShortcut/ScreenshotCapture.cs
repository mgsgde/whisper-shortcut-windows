using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WhisperShortcut;

/// <summary>
/// The Screenshot hotkey: Windows' own snip UI (<c>ms-screenclip:</c>, the same as Win+Umschalt+S) picks the area,
/// the image comes over the clipboard (<see cref="ClipboardPaster.CaptureSnipAsync"/>) and leaves as PNG.
/// </summary>
internal static class ScreenshotCapture
{
    // Win10 / early Win11: ScreenClippingHost draws the overlay; newer Win11 builds hand it to the Snipping Tool.
    private static readonly string[] SnipProcesses = { "ScreenClippingHost", "SnippingTool" };

    /// <summary>Opens the snip UI and returns the captured area as a PNG image, or null on cancel, timeout or an unusable image.</summary>
    public static async Task<Core.ScreenshotImage?> CaptureAsync(ClipboardPaster paster, CancellationToken ct)
    {
        var raw = await paster.CaptureSnipAsync(Launch, SnipUiRunning, ct);
        if (raw == null) return null;
        var png = raw.Value.IsPng ? raw.Value.Data : DibToPng(raw.Value.Data);
        return Core.ScreenshotImage.FromPng(png);
    }

    private static bool Launch()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
            return true;
        }
        catch (Exception e)
        {
            Core.Log.Error("Snip: ms-screenclip could not be started", e);
            return false;
        }
    }

    private static bool SnipUiRunning()
    {
        foreach (var name in SnipProcesses)
        {
            var found = Process.GetProcessesByName(name);
            bool any = found.Length > 0;
            foreach (var p in found) p.Dispose();
            if (any) return true;
        }
        return false;
    }

    private static byte[]? DibToPng(byte[] dib)
    {
        var bmp = Core.DibImage.ToBmp(dib);
        if (bmp == null) return null;
        try
        {
            using var input = new MemoryStream(bmp);
            using var image = new Bitmap(input);
            using var output = new MemoryStream();
            image.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception e) when (e is ArgumentException or ExternalException)
        {
            Core.Log.Error($"Snip: DIB could not be converted ({e.GetType().Name})");
            return null;
        }
    }
}
