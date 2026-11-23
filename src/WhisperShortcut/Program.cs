namespace WhisperShortcut;

internal static class Program
{
    /// <summary>The single-instance mutex; <c>--uninstall</c> takes it too.</summary>
    internal static string MutexName => Core.AppIdentity.Current.MutexName; // Dev: Local\WhisperShortcut.Windows.Dev

    [STAThread]
    static void Main(string[] args)
    {
        // --log <path>: write the log there instead (the VM test loop points it at the shared Downloads
        // folder, so the Mac reads it without prlctl exec, which flashes a console window in the VM).
        int logArg = Array.IndexOf(args, "--log");
        Core.Log.Init(logArg >= 0 && logArg + 1 < args.Length ? args[logArg + 1] : Core.Log.DefaultPath);

        // Runs inside the staged new exe: swap files, no mutex, no UI.
        int install = Array.IndexOf(args, "--install-update");
        if (install >= 0 && install + 1 < args.Length)
            Environment.Exit(UpdateInstaller.RunInstall(args[install + 1]));

        // The uninstaller (the Apps entry and the settings button start it): its own flow, no tray app.
        if (args.Contains("--uninstall"))
            Environment.Exit(Uninstaller.Run());

        // One instance only: a second one could not register the hotkeys anyway.
        // After an update (or a move to the fixed place) the old process may still be shutting down, so wait for it.
        bool afterUpdate = args.Contains("--after-update");
        bool moved = args.Contains("--moved");
        using var mutex = new Mutex(false, MutexName);
        bool first;
        try { first = mutex.WaitOne(afterUpdate || moved ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { first = true; }
        if (afterUpdate || moved)
            Core.Log.Info((moved ? "Started after the move" : "Started after update") + (first ? "" : ", but the old instance is still running"));
        if (!first)
        {
            Core.Log.Info("Second instance started, exiting");
            // Like the Mac's reopen: starting the app again (Start menu, search) opens its settings. The MessageBox is only
            // for when the running instance cannot be reached.
            InstanceSignals.AllowForeground();
            if (InstanceSignals.Signal(InstanceSignals.ShowName)) return;
            MessageBox.Show($"{Core.AppIdentity.Current.DisplayName} läuft bereits. Das Symbol ist unten rechts im Infobereich der Taskleiste.",
                Core.AppIdentity.Current.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Before the slow TrayApp constructor, so a second start during startup is not lost.
        using var signals = new InstanceSignals();

        // Switch from the other version (tray: "Zur Dev-Version wechseln" / "Zur normalen Version wechseln"): it quits first,
        // so only one of the two registers the global hotkeys (TrayApp's constructor).
        if (args.Contains("--takeover")) InstanceSignals.TakeOverFromOther(TimeSpan.FromSeconds(10));

        UpdateInstaller.CleanupAfterStart();
        SelfExtractCleanup.CleanupStaleInBackground();
        int feedArg = Array.IndexOf(args, "--update-feed");
        if (feedArg >= 0 && feedArg + 1 < args.Length)
        {
            var feed = args[feedArg + 1];
            if (Core.NetworkPolicy.IsAllowedDownloadUrl(feed))
            {
                Core.UpdateCheck.ApiUrl = feed;
                Core.Log.Info($"Update feed override {Core.UpdateCheck.ApiUrl}");
            }
            else
            {
                Core.Log.Error("Update feed override rejected (need https or localhost)");
            }
        }

        string? testWav = TestWavPath(args);
        bool fakeGemini = args.Contains("--fake-gemini");
        string? smokeOut = SmokeOutPath(args);
        if (smokeOut == "")
        {
            Core.Log.Error("--smoke-dictate needs an output path");
            Environment.Exit(2);
        }
        if (smokeOut != null && testWav == null)
        {
            Core.Log.Error("--smoke-dictate needs a test wav (--test-wav or WSW_TEST_WAV)");
            Environment.Exit(2);
        }
        Core.Log.Info($"Start {typeof(Program).Assembly.GetName().Version} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})"
            + (testWav != null ? $", test wav {Path.GetFileName(testWav)}" : "") + (fakeGemini ? ", fake Gemini" : "")
            + (smokeOut != null ? ", smoke dictation" : ""));

        Application.ThreadException += (_, e) => Core.Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Core.Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        var app = new TrayApp(testWav, fakeGemini, smokeOut);
        if (args.Contains("--settings")) app.OpenSettingsOnStart();
        if (args.Contains("--welcome")) app.OpenWelcome();
        signals.Listen(app.ShowSettingsFromSecondStart, app.QuitFromUninstall);
        Application.Run(app);
        // The smoke run sets Environment.ExitCode before it quits; returning from Main keeps it as the process exit code.
        Environment.Exit(Environment.ExitCode);
    }

    /// <summary>
    /// <c>--smoke-dictate &lt;path&gt;</c>: headless CI run of the whole dictation path on the test wav; the text goes to this file
    /// (relative to the current directory). Null when absent, "" when the path is missing.
    /// </summary>
    private static string? SmokeOutPath(string[] args)
    {
        int i = Array.IndexOf(args, "--smoke-dictate");
        if (i < 0) return null;
        if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) return "";
        return Path.GetFullPath(args[i + 1]);
    }

    /// <summary>
    /// <c>--test-wav &lt;path&gt;</c> (relative to the exe's folder) or env WSW_TEST_WAV: read this file instead
    /// of the microphone. That is how the VM tests run without a mic.
    /// </summary>
    private static string? TestWavPath(string[] args)
    {
        int i = Array.IndexOf(args, "--test-wav");
        string? path = i >= 0 && i + 1 < args.Length ? args[i + 1] : Environment.GetEnvironmentVariable("WSW_TEST_WAV");
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
    }
}
