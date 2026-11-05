using System.Runtime.InteropServices;
using System.Text;

namespace WhisperShortcut;

/// <summary>
/// Where this exe lives and whether it may register itself (autostart, Start menu, Uninstall entry).
/// The classification is pure and lives in <see cref="Core.InstallLocation"/>; this adds the real folders.
/// </summary>
internal static class InstallState
{
#if DEBUG
    private const bool DebugBuild = true;
#else
    private const bool DebugBuild = false;
#endif

    /// <summary>Set once at start for test runs (--test-wav, --fake-gemini): they must not register anything.</summary>
    public static bool TestRun { get; set; }

    public static string ExePath => Environment.ProcessPath ?? "";

    public static string CanonicalDir =>
        Core.InstallLocation.CanonicalDir(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string CanonicalExe => Path.Combine(CanonicalDir, Core.UpdateInstallPath.ExeName);

    public static Core.InstallPlace Place => PlaceOf(ExePath);

    public static Core.InstallPlace PlaceOf(string exe) => Core.InstallLocation.Classify(
        exe, LongPath(Path.GetTempPath()), DownloadsFolder(), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), CanonicalDir);

    /// <summary>The one "may register" rule (see <see cref="Core.InstallLocation.MayRegister"/>).</summary>
    public static bool MayRegister => Core.InstallLocation.MayRegister(Place, DebugBuild, TestRun);

    /// <summary>The user's Downloads folder (it can be moved, so ask Windows), or null.</summary>
    private static string? DownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch (Exception) { return null; }
    }

    /// <summary>%TEMP% can come as a short 8.3 path (ADMINI~1); the exe path never does.</summary>
    private static string LongPath(string path)
    {
        try
        {
            var sb = new StringBuilder(1024);
            uint n = GetLongPathName(path, sb, (uint)sb.Capacity);
            return n > 0 && n < sb.Capacity ? sb.ToString() : path;
        }
        catch (Exception) { return path; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);
}
