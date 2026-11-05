using System.Diagnostics;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut;

/// <summary>
/// <c>--uninstall</c>: the app has no installer, so this is its uninstaller. Asks first, stops the running instance,
/// removes everything the app wrote outside its own folder (autostart, Start menu, Apps entry, AI-app configs, app data,
/// self-extract copies) and, when the exe sits in the canonical folder, deletes it after this process has exited.
/// </summary>
internal static class Uninstaller
{
    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        var exe = InstallState.ExePath;
        bool deleteExe = InstallState.Place == Core.InstallPlace.Canonical && !string.IsNullOrEmpty(exe);

        var what = "Entfernt werden: der Start mit Windows, der Eintrag im Startmenü und unter „Apps“ in den Windows-Einstellungen, "
            + "alle Einstellungen, der API-Schlüssel, das Glossar, die Offline-Modelle und das Protokoll sowie der Eintrag von "
            + "WhisperShortcut in Claude Desktop und Claude Code (falls verbunden).\n\n"
            + (deleteExe
                ? "Auch die Programmdatei wird gelöscht. "
                : $"Die Datei {Path.GetFileName(exe)} bleibt liegen; du kannst sie danach selbst löschen. ")
            + "Das lässt sich nicht rückgängig machen.";
        if (ChoiceDialog.Ask(null!, "WhisperShortcut deinstallieren?", what, new[] { "Deinstallieren", "Abbrechen" }, danger: true) != 0)
            return 1;
        Core.Log.Info("Uninstall: confirmed");

        // The running instance gets the quit signal; owning the mutex proves it is gone and keeps a new start out meanwhile.
        using var mutex = new Mutex(false, Program.MutexName);
        if (!Own(mutex, TimeSpan.Zero))
        {
            InstanceSignals.Signal(InstanceSignals.QuitName);
            if (!Own(mutex, TimeSpan.FromSeconds(5)))
            {
                Core.Log.Error("Uninstall: running instance did not quit");
                MessageBox.Show("WhisperShortcut läuft noch und hat sich nicht beenden lassen. Rechtsklick auf das Symbol unten rechts "
                    + "und „WhisperShortcut beenden“, danach noch einmal deinstallieren.", "WhisperShortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }

        var problems = new List<string>();
        void Step(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { Core.Log.Error($"Uninstall: {name} failed", ex); problems.Add(name); }
        }

        Step("Autostart", () => { if (!AutoStart.Disable()) throw new IOException("autostart"); });
        Step("Startmenü", StartMenu.Remove);
        Step("Apps-Eintrag", UninstallEntry.Remove);
        Step("KI-Apps", RemoveAiAppEntries);
        var extractRoot = SelfExtractCleanup.Current()?.Root;
        // Last: the log goes with the app data. The Reset also stops further writes to it.
        Step("App-Daten", () => Core.AppData.Reset());

        if (problems.Count > 0)
        {
            MessageBox.Show("Nicht alles ließ sich entfernen: " + string.Join(", ", problems) + ". Meist hilft es, WhisperShortcut-Fenster und "
                + "KI-Apps zu schließen und noch einmal zu deinstallieren.", "WhisperShortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            MessageBox.Show(deleteExe
                ? "WhisperShortcut ist deinstalliert."
                : $"WhisperShortcut ist deinstalliert. Die Datei {Path.GetFileName(exe)} kannst du jetzt selbst löschen.",
                "WhisperShortcut", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // After the message: while this process runs, its exe and its extracted DLLs are locked.
        StartDelayedCleanup(deleteExe ? exe : null, extractRoot);
        return 0;
    }

    private static bool Own(Mutex mutex, TimeSpan wait)
    {
        try { return mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>Takes our entry out of Claude Desktop's and Claude Code's config files, leaving everything else (backup beside).</summary>
    private static void RemoveAiAppEntries()
    {
        var paths = new[]
        {
            McpClientSetup.ClaudeDesktopConfigPath(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            McpClientSetup.ClaudeCodeUserConfigPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        };
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var removed = McpClientSetup.RemoveServer(File.ReadAllText(path));
                if (removed == null) continue; // no entry, or a file we cannot read as JSON: leave it alone
                McpClientSetup.WriteWithBackup(path, _ => removed);
                Core.Log.Info($"Uninstall: AI app entry removed from {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                Core.Log.Error($"Uninstall: AI app config {Path.GetFileName(path)} not changed", ex);
            }
        }
    }

    /// <summary>A hidden cmd that waits for this process to end, then deletes the exe, its folder and the extract folder.</summary>
    private static void StartDelayedCleanup(string? exe, string? extractRoot)
    {
        var parts = new List<string> { "ping -n 4 127.0.0.1 >nul" };
        if (exe != null)
        {
            parts.Add($"del /f /q \"{exe}\" \"{exe}.old\"");
            parts.Add($"rmdir \"{Path.GetDirectoryName(exe)}\"");
        }
        if (extractRoot != null) parts.Add($"rmdir /s /q \"{extractRoot}\"");
        if (parts.Count == 1) return;
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", parts))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
        }
        catch (Exception ex) { Core.Log.Error("Uninstall: delayed cleanup did not start", ex); }
    }
}
