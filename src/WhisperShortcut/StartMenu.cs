using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WhisperShortcut;

/// <summary>
/// A per-user Start menu entry, so Windows search finds the app like Spotlight finds the Mac app in /Applications.
/// The single-file exe has no installer: the entry is written on every start and follows the exe when it moves or
/// is updated. No admin needed (%APPDATA%\Microsoft\Windows\Start Menu\Programs).
/// </summary>
internal static class StartMenu
{
    private const string Name = "WhisperShortcut";

    /// <summary>The .lnk in the user's Start menu.</summary>
    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");

    /// <summary>
    /// Creates the entry, or points it at this exe when it targets another path. Never throws. Skipped where
    /// <see cref="InstallState.MayRegister"/> is false (test runs, debug builds, temporary copies), which would
    /// otherwise send the user's Start menu to a build folder or a vanishing path.
    /// </summary>
    public static void EnsureEntry(bool testRun)
    {
        InstallState.TestRun = testRun;
        if (!InstallState.MayRegister) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            if (Write(ShortcutPath, exe)) Core.Log.Info("Start menu entry written");
        }
        catch (Exception ex)
        {
            Core.Log.Error("Start menu entry failed", ex);
        }
    }

    /// <summary>Removes the entry (uninstall). Never throws.</summary>
    public static void Remove()
    {
        try { File.Delete(ShortcutPath); }
        catch (Exception ex) { Core.Log.Error("Start menu entry removal failed", ex); }
    }

    /// <summary>Writes <paramref name="lnk"/> pointing at <paramref name="exe"/> unless it already does. True when written.</summary>
    internal static bool Write(string lnk, string exe)
    {
        if (File.Exists(lnk) && string.Equals(ReadTarget(lnk), exe, StringComparison.OrdinalIgnoreCase)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetDescription("Diktieren und Texte bearbeiten per Tastenkürzel");
            link.SetIconLocation(exe, 0);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.ReleaseComObject(link); }
        return true;
    }

    /// <summary>The exe a shortcut points at, or null when it cannot be read.</summary>
    internal static string? ReadTarget(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new char[1024];
            link.GetPath(target, target.Length, IntPtr.Zero, 0);
            var s = new string(target);
            int end = s.IndexOf('\0');
            return end >= 0 ? s[..end] : s;
        }
        catch (Exception) { return null; }
        finally { Marshal.ReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray)] char[] pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray)] char[] pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray)] char[] pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray)] char[] pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray)] char[] pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
