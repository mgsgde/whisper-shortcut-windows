using System.Diagnostics;

namespace WhisperShortcut;

/// <summary>
/// Removes the native-DLL copies that older builds left in %TEMP%\.net\&lt;exe name&gt;\&lt;hash&gt;\ (the single-file
/// exe extracts a new one per build). The folder in use is taken from a loaded module's path; if it cannot be
/// determined, nothing is deleted.
/// </summary>
internal static class SelfExtractCleanup
{
    /// <summary>The extraction folders (root and the one in use) as seen from the loaded modules, or null.</summary>
    public static (string Root, string InUse)? Current()
    {
        try
        {
            var app = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
            using var me = Process.GetCurrentProcess();
            foreach (ProcessModule m in me.Modules)
            {
                var found = Core.SelfExtractFolders.Locate(m.FileName, app);
                if (found != null) return found;
            }
        }
        catch (Exception ex) { Core.Log.Error("Self-extract folder lookup failed", ex); }
        return null;
    }

    /// <summary>Best effort, on a background thread: deletes the other hash folders. Locked ones fail silently (logged once).</summary>
    public static void CleanupStaleInBackground()
    {
        Task.Run(() =>
        {
            try
            {
                if (Current() is not { } cur || !Directory.Exists(cur.Root)) return;
                int failed = 0, removed = 0;
                foreach (var dir in Core.SelfExtractFolders.Stale(cur.Root, cur.InUse, Directory.GetDirectories(cur.Root)))
                {
                    try { Directory.Delete(dir, recursive: true); removed++; }
                    catch (Exception) { failed++; }
                }
                if (removed + failed > 0) Core.Log.Info($"Self-extract cleanup: {removed} old folders removed, {failed} in use");
            }
            catch (Exception ex) { Core.Log.Error("Self-extract cleanup failed", ex); }
        });
    }
}
