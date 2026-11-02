using System.Buffers.Binary;

namespace WhisperShortcut.Core;

/// <summary>An image to send along with one request. Data = PNG bytes. In memory only (patient data).</summary>
public sealed record ScreenshotImage(byte[] Data, string MimeType)
{
    /// <summary>Above this the image is not sent (Gemini inline request limit is 20 MB; base64 adds a third).</summary>
    public const long MaxBytes = 14L * 1024 * 1024;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// A PNG image, or null when <paramref name="data"/> is null/empty, lacks the 8-byte PNG signature,
    /// or is larger than <see cref="MaxBytes"/>.
    /// </summary>
    public static ScreenshotImage? FromPng(byte[]? data)
    {
        if (data == null || data.Length < PngSignature.Length || data.Length > MaxBytes) return null;
        return data.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)
            ? new ScreenshotImage(data, "image/png")
            : null;
    }
}

/// <summary>Clipboard CF_DIB / CF_DIBV5 memory block to .bmp file bytes (so System.Drawing can load it and re-encode as PNG).</summary>
public static class DibImage
{
    private const int FileHeaderSize = 14;

    /// <summary>
    /// Prepends a 14-byte BITMAPFILEHEADER. Returns null when <paramref name="dib"/> is null, shorter than 40 bytes,
    /// has biSize below 40, or the header claims a pixel offset beyond the data.
    /// </summary>
    public static byte[]? ToBmp(byte[]? dib)
    {
        if (dib == null || dib.Length < 40) return null;
        var biSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (biSize < 40) return null;
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        var clrUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));

        // V4/V5 headers carry the channel masks inside; only the plain 40-byte header has them after it.
        long masks = biSize == 40 && compression == 3 ? 12 : 0;
        long colors = clrUsed != 0 ? clrUsed : bitCount <= 8 ? 1L << bitCount : 0;
        long offBits = FileHeaderSize + biSize + masks + colors * 4;
        if (offBits - FileHeaderSize > dib.Length) return null;

        var bmp = new byte[FileHeaderSize + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        // Bytes 6..9 are the two reserved shorts, left 0.
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)offBits);
        dib.CopyTo(bmp, FileHeaderSize);
        return bmp;
    }
}

/// <summary>Holds one screenshot for the next Dictate Prompt / template turn. Used once (Take), dropped after <see cref="Lifetime"/> unused.</summary>
public sealed class ScreenshotHold
{
    /// <summary>How long an unused screenshot is kept.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly Func<DateTime> _clock;
    private ScreenshotImage? _image;
    private DateTime _heldAt;

    /// <summary>Creates a hold; <paramref name="clock"/> returns the current UTC time (tests pass a fake).</summary>
    public ScreenshotHold(Func<DateTime> clock) => _clock = clock;

    /// <summary>Holds <paramref name="image"/>, replacing an earlier one; the 5 minutes start now.</summary>
    public void Hold(ScreenshotImage image)
    {
        _image = image;
        _heldAt = _clock();
    }

    /// <summary>True when an unexpired image is held. An expired image is dropped (reference cleared) as a side effect.</summary>
    public bool HasImage
    {
        get
        {
            DropIfExpired();
            return _image != null;
        }
    }

    /// <summary>Returns the held image and clears it (one turn only); null when none or expired (expired is dropped).</summary>
    public ScreenshotImage? Take()
    {
        DropIfExpired();
        var image = _image;
        _image = null;
        return image;
    }

    /// <summary>Drops the held image (offline switched on, setting turned off).</summary>
    public void Clear() => _image = null;

    /// <summary>Drops the image when expired; returns true when something was dropped (a UI timer calls this).</summary>
    public bool DropIfExpired()
    {
        if (_image == null || _clock() - _heldAt < Lifetime) return false;
        _image = null;
        return true;
    }
}

/// <summary>Whether screenshots may go out with a request.</summary>
public static class ScreenshotPolicy
{
    /// <summary>
    /// True only when <see cref="Settings.SendScreenshots"/> is on, <see cref="Settings.OfflineMode"/> is off and
    /// <see cref="Settings.EffectiveTextEngine"/> is not <see cref="Engine.Local"/> (nothing may leave the PC offline,
    /// and the local model cannot see images).
    /// </summary>
    public static bool Allowed(Settings settings) =>
        settings.SendScreenshots && !settings.OfflineMode && settings.EffectiveTextEngine != Engine.Local;
}

/// <summary>Text sent right before the image in the user turn.</summary>
public static class ScreenshotPrompt
{
    /// <summary>Tells the model what the image is and that it is context only.</summary>
    public const string Header = "SCREENSHOT (attached image): the user's screen, e.g. values or a record in the program they work in. Context only: use what it shows where the instruction or the format calls for it, and never invent what it does not show. Do not describe or mention the screenshot itself.";
}

/// <summary>How waiting for the Windows snip UI ended (or <see cref="Waiting"/> while it has not).</summary>
public enum SnipStep
{
    /// <summary>Keep polling.</summary>
    Waiting,
    /// <summary>The clipboard holds the new image.</summary>
    Captured,
    /// <summary>The user closed the snip UI or copied something else.</summary>
    Cancelled,
    /// <summary>Nothing happened within <see cref="SnipWatch.Timeout"/>.</summary>
    TimedOut,
}

/// <summary>Decides, poll by poll, how waiting for the Windows snip UI ends. Pure logic; the app polls every ~100 ms.</summary>
public sealed class SnipWatch
{
    /// <summary>Longest wait after launching the snip UI.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The snip UI process was seen and is gone for this long without an image: the user cancelled.</summary>
    public static readonly TimeSpan ClosedGrace = TimeSpan.FromSeconds(1.5);

    /// <summary>The clipboard changed but holds no image for this long: someone copied something else, give up.</summary>
    public static readonly TimeSpan NoImageGrace = TimeSpan.FromSeconds(2);

    private bool _uiSeen;
    private TimeSpan? _uiGoneSince;
    private TimeSpan? _noImageSince;

    /// <summary>
    /// One poll. <paramref name="elapsed"/>: since the snip UI was launched. <paramref name="clipboardChanged"/>: sequence
    /// number differs from the one before launch. <paramref name="hasImage"/>: the clipboard offers PNG or DIB/DIBV5.
    /// <paramref name="snipUiRunning"/>: the snip overlay process is running.
    /// </summary>
    public SnipStep Next(TimeSpan elapsed, bool clipboardChanged, bool hasImage, bool snipUiRunning)
    {
        if (snipUiRunning)
        {
            _uiSeen = true;
            _uiGoneSince = null;
        }
        else if (_uiSeen && _uiGoneSince == null)
        {
            _uiGoneSince = elapsed;
        }

        if (clipboardChanged && hasImage) return SnipStep.Captured;
        if (elapsed >= Timeout) return SnipStep.TimedOut;

        if (clipboardChanged)
        {
            _noImageSince ??= elapsed;
            if (elapsed - _noImageSince.Value >= NoImageGrace) return SnipStep.Cancelled;
        }
        else
        {
            _noImageSince = null;
        }

        // WHY: if the process was never seen (detection can fail on some Windows builds) only the timeout ends the wait.
        if (_uiGoneSince is { } gone && elapsed - gone >= ClosedGrace) return SnipStep.Cancelled;
        return SnipStep.Waiting;
    }
}
