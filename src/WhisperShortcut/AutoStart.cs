using Microsoft.Win32;

namespace WhisperShortcut;

/// <summary>
/// Start with Windows through HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// That needs no admin and no installer: the single-file exe is the app, and a per-user
/// Run value is enough to launch it at logon.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WhisperShortcut";

    /// <summary>True when the Run value exists. A registry failure counts as off, so the settings window can still open.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) != null;
            }
            catch (Exception ex)
            {
                Core.Log.Error("Autostart: reading the Run key failed", ex);
                return false;
            }
        }
    }

    /// <summary>Writes the Run value as the quoted path of this exe. False on failure; never throws.</summary>
    public static bool Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) throw new InvalidOperationException("Run key missing");
            key.SetValue(ValueName, QuotedPath());
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.Error("Autostart: enabling failed", ex);
            return false;
        }
    }

    /// <summary>Removes the Run value. False on failure; never throws. Already absent counts as success.</summary>
    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.Error("Autostart: disabling failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Rewrites the Run value when it still points at an exe that is no longer this process.
    /// The single-file build is often started from Downloads and moved later; the old path would
    /// silently stop launching anything.
    /// </summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (key.GetValue(ValueName) is not string stored) return;
            var current = QuotedPath();
            if (string.Equals(stored, current, StringComparison.Ordinal)) return;
            key.SetValue(ValueName, current);
            Core.Log.Info($"Autostart: exe moved, Run entry rewritten to {current}");
        }
        catch (Exception ex)
        {
            Core.Log.Error("Autostart: refreshing the path failed", ex);
        }
    }

    private static string QuotedPath() => $"\"{Environment.ProcessPath}\"";
}
