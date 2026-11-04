using System.ComponentModel;
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
        // Our own picker first (instant, like the Mac); Windows' snip UI only if capturing the screen failed.
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var picked = await SnipOverlay.PickAsync(ct);
            if (picked == null) { Core.Log.Info("Snip: cancelled"); return null; }
            using (var input = new MemoryStream(picked))
            using (var image = new Bitmap(input))
                if (!paster.PutImage(picked, image)) Core.Log.Warn("Snip: clipboard busy, image not left on the clipboard");
            Core.Log.Info($"Snip: PNG {picked.Length / 1024} KB in {watch.ElapsedMilliseconds} ms, left on the clipboard");
            return Core.ScreenshotImage.FromPng(picked);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is ExternalException or ArgumentException or Win32Exception or InvalidOperationException)
        {
            Core.Log.Error("Snip: own picker failed, using the Windows snip UI", e);
        }
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
