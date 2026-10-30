using System.ComponentModel;
using System.Diagnostics;

namespace WhisperShortcut;

/// <summary>Swaps the running exe for a downloaded, already verified one. Windows process plumbing only.</summary>
internal static class UpdateInstaller
{
    /// <summary>%LOCALAPPDATA%\WhisperShortcut\update: where the new exe is staged.</summary>
    public static string StagingDir => Path.Combine(Core.AppData.LocalFolder, "update");

    public static string CurrentExe => Environment.ProcessPath!;

    /// <summary>False when the exe's folder needs admin rights (e.g. C:\Program Files).</summary>
    public static bool CanWriteNextToExe()
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(CurrentExe)!, Path.GetRandomFileName());
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the staged exe as installer (elevated if needed), then starts the new version.
    /// True: the caller must exit the app now. Awaited, not blocking: the admin prompt can stay open for minutes.
    /// </summary>
    public static async Task<bool> ApplyAsync(string stagedExe)
    {
        try
        {
            bool elevate = !CanWriteNextToExe();
            Core.Log.Info($"Update: starting installer, elevation {(elevate ? "needed" : "not needed")}");
            var psi = new ProcessStartInfo(stagedExe)
            {
                Arguments = $"--install-update \"{CurrentExe}\"",
                UseShellExecute = true,
                WorkingDirectory = StagingDir,
            };
            if (elevate) psi.Verb = "runas";
            // Off the UI thread: with "runas", Process.Start only returns once the admin prompt is answered,
            // which can take minutes (someone with an admin account has to come over). No timeout for that.
            using var p = await Task.Run(() => Process.Start(psi));
            if (p == null) { Core.Log.Error("Update: installer did not start"); return false; }
            // Starting the new exe can take minutes while antivirus scans it; the swap itself takes seconds.
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            {
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { Core.Log.Error("Update: installer timed out"); return false; }
            }
            if (p.ExitCode != 0) { Core.Log.Error($"Update: installer exit code {p.ExitCode}"); return false; }

            Core.Log.Info("Update: installed, restarting");
            Process.Start(new ProcessStartInfo(CurrentExe) { Arguments = "--after-update", UseShellExecute = false });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Core.Log.Info("Update: user declined the admin prompt");
            return false;
        }
        catch (Exception ex)
        {
            Core.Log.Error("Update: apply failed", ex);
            return false;
        }
    }

    /// <summary>Runs inside the staged exe: replaces <paramref name="targetExe"/> with this file. 0 = ok, 1 = failed.</summary>
    public static int RunInstall(string targetExe)
    {
        string self = Environment.ProcessPath!;
        string old = targetExe + ".old";
        bool moved = false;
        try
        {
            try { if (File.Exists(old)) File.Delete(old); } catch (Exception) { }
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(targetExe, old); moved = true; break; }
                catch (IOException) when (attempt < 5) { Thread.Sleep(200); }
            }
            File.Copy(self, targetExe);
            Core.Log.Info("Update: exe replaced");
            return 0;
        }
        catch (Exception ex)
        {
            Core.Log.Error("Update: replacing the exe failed", ex);
            if (moved)
            {
                try { if (File.Exists(targetExe)) File.Delete(targetExe); File.Move(old, targetExe); }
                catch (Exception ex2) { Core.Log.Error("Update: rollback failed", ex2); }
            }
            return 1;
        }
    }

    /// <summary>Best effort: removes the old exe and the staging folder after a start. Never throws.</summary>
    public static void CleanupAfterStart()
    {
        try { File.Delete(CurrentExe + ".old"); } catch (Exception) { }
        try { if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, recursive: true); } catch (Exception) { }
    }
}
