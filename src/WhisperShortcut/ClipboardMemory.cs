using System.Runtime.InteropServices;
using static WhisperShortcut.NativeMethods;

namespace WhisperShortcut;

/// <summary>
/// The HGLOBAL plumbing of the clipboard in one place: a block handed to SetClipboardData belongs to the system,
/// one that could not be handed over must be freed, and every lock needs its unlock. ClipboardPaster only decides
/// what goes on the clipboard, never how the memory is shaped.
/// </summary>
internal static class ClipboardMemory
{
    /// <summary>A moveable block with the UTF-16 text and its terminating null; <c>IntPtr.Zero</c> when it could not be made.</summary>
    public static IntPtr AllocText(string text)
    {
        IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((text.Length + 1) * 2));
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        IntPtr ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) { GlobalFree(handle); return IntPtr.Zero; }
        Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
        Marshal.WriteInt16(ptr, text.Length * 2, 0);
        GlobalUnlock(handle);
        return handle;
    }

    /// <summary>A moveable block with a copy of <paramref name="data"/>; <c>IntPtr.Zero</c> when it could not be made.</summary>
    public static IntPtr AllocBytes(byte[] data)
    {
        IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)data.Length);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        IntPtr ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) { GlobalFree(handle); return IntPtr.Zero; }
        Marshal.Copy(data, 0, ptr, data.Length);
        GlobalUnlock(handle);
        return handle;
    }

    /// <summary>
    /// The bytes of a memory block. Null when it is empty (not a memory block after all), larger than
    /// <paramref name="maxSize"/>, or cannot be locked.
    /// </summary>
    public static byte[]? ReadBytes(IntPtr handle, long maxSize)
    {
        long size = (long)GlobalSize(handle);
        if (size <= 0 || size > maxSize) return null;
        IntPtr ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }

    /// <summary>
    /// SetClipboardData for a block from this class (the clipboard must be open). On success the system owns the
    /// memory; on failure it is freed here, so the caller never leaks it.
    /// </summary>
    public static bool SetData(uint format, IntPtr handle)
    {
        if (SetClipboardData(format, handle) != IntPtr.Zero) return true;
        GlobalFree(handle);
        return false;
    }

    /// <summary>Keeps what was just set out of the Win+V history and the cloud clipboard (the clipboard must be open).</summary>
    public static void MarkPrivate()
    {
        SetDword("CanIncludeInClipboardHistory", 0);
        SetDword("CanUploadToCloudClipboard", 0);
    }

    private static void SetDword(string format, int value)
    {
        uint id = RegisterClipboardFormat(format);
        if (id == 0) return;
        IntPtr handle = AllocBytes(BitConverter.GetBytes(value));
        if (handle == IntPtr.Zero) return;
        SetData(id, handle);
    }
}
