namespace WhisperShortcut;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // --log <path>: write the log there instead (the VM test loop points it at the shared Downloads
        // folder, so the Mac reads it without prlctl exec, which flashes a console window in the VM).
        int logArg = Array.IndexOf(args, "--log");
        Core.Log.Init(logArg >= 0 && logArg + 1 < args.Length ? args[logArg + 1] : Core.Log.DefaultPath);

        // One instance only: a second one could not register the hotkeys anyway.
        using var mutex = new Mutex(true, @"Local\WhisperShortcut.Windows", out bool first);
        if (!first)
        {
            Core.Log.Info("Second instance started, exiting");
            MessageBox.Show("WhisperShortcut läuft bereits. Das Symbol ist unten rechts im Infobereich der Taskleiste.",
                "WhisperShortcut", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string? testWav = TestWavPath(args);
        bool fakeGemini = args.Contains("--fake-gemini");
        Core.Log.Info($"Start {typeof(Program).Assembly.GetName().Version} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})"
            + (testWav != null ? $", test wav {Path.GetFileName(testWav)}" : "") + (fakeGemini ? ", fake Gemini" : ""));

        Application.ThreadException += (_, e) => Core.Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Core.Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        var app = new TrayApp(testWav, fakeGemini);
        if (args.Contains("--settings")) app.OpenSettingsOnStart();
        if (args.Contains("--welcome")) app.OpenWelcome();
        Application.Run(app);
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
