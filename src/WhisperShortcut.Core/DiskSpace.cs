namespace WhisperShortcut.Core;

/// <summary>HRESULT and German user text for a full disk, shared by settings, the recording spill and downloads.</summary>
public static class DiskSpace
{
    /// <summary>ERROR_DISK_FULL (0x80070070).</summary>
    public const int DiskFullHResult = unchecked((int)0x80070070);

    /// <summary>ENOSPC as a Win32 HRESULT (0x8007001C) or the raw errno.</summary>
    public const int EnospcHResult = unchecked((int)0x8007001C);

    /// <summary>Settings could not be written because the drive is full.</summary>
    public const string SettingsMessage =
        "Der Datenträger ist voll. Die Einstellungen konnten nicht gespeichert werden.";

    /// <summary>The live recording continues in memory; a crash would lose it.</summary>
    public const string RecordingMessage =
        "Der Datenträger ist voll. Die Aufnahme läuft weiter, geht bei einem Absturz aber verloren.";

    /// <summary>True when <paramref name="ex"/> is Windows ERROR_DISK_FULL or Unix ENOSPC.</summary>
    public static bool IsFull(IOException ex)
    {
        var hr = ex.HResult;
        return hr == DiskFullHResult || hr == EnospcHResult || hr == 28;
    }

    /// <summary><see cref="IsFull(IOException)"/> for any exception (false when it is not an <see cref="IOException"/>).</summary>
    public static bool IsFull(Exception ex) => ex is IOException io && IsFull(io);
}

/// <summary>A write failed because the drive is full. <see cref="Exception.Message"/> is German user text.</summary>
public sealed class DiskFullException : IOException
{
    /// <summary>Creates the error; HResult stays ERROR_DISK_FULL so callers can still match it.</summary>
    public DiskFullException(string message, Exception? inner = null) : base(message, inner)
    {
        HResult = DiskSpace.DiskFullHResult;
    }
}
