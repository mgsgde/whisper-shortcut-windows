namespace WhisperShortcut;

/// <summary>
/// Looks for a new version a minute after start and once a day (Settings.AutoUpdateCheck), remembers what
/// it found, and owns the one update window. Like Sparkle on the Mac: a check only leads to a notice,
/// nothing is downloaded or installed until the user clicks "Jetzt installieren".
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private readonly Core.Settings _settings;
    private readonly Func<bool> _idle;
    private readonly Action _quit;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };
    private UpdateDialog? _dialog;

    /// <param name="idle">True when no recording or processing runs: only then may the app restart.</param>
    /// <param name="quit">Ends this instance after the new exe is in place and started.</param>
    public UpdateService(Core.Settings settings, Func<bool> idle, Action quit)
    {
        _settings = settings;
        _idle = idle;
        _quit = quit;
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = 24 * 60 * 60 * 1000;
            if (_settings.AutoUpdateCheck) await CheckAsync(manual: false);
        };
    }

    public static string CurrentVersion => Application.ProductVersion.Split('+')[0];

    /// <summary>The newer release found by the last check, or null.</summary>
    public Core.UpdateInfo? Available { get; private set; }

    /// <summary>A check found a version that was not known before (once per version and run).</summary>
    public event Action? Found;

    public bool IsIdle => _idle();

    public void Start() => _timer.Start();

    /// <summary>
    /// Asks GitHub. Returns the newer release or null. A failed automatic check only logs; a manual one
    /// (<paramref name="manual"/>) lets the <see cref="HttpRequestException"/> through so the window can say so.
    /// </summary>
    public async Task<Core.UpdateInfo?> CheckAsync(bool manual)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var latest = await Core.UpdateCheck.CheckAsync(http, CancellationToken.None);
            Core.Log.Info($"Update check ({(manual ? "manual" : "automatic")}): latest {latest?.Version ?? "none"}, running {CurrentVersion}");
            var newer = latest != null && Core.UpdateCheck.IsNewer(latest.Version, CurrentVersion) ? latest : null;
            bool fresh = newer != null && newer.Version != Available?.Version;
            Available = newer;
            if (fresh) Found?.Invoke();
            return newer;
        }
        catch (Exception e) when (!manual && e is HttpRequestException or TaskCanceledException)
        {
            Core.Log.Error($"Automatic update check failed: {e.GetType().Name}");
            return null;
        }
    }

    /// <summary>Shows the update window for <see cref="Available"/> (brings an open one to the front).</summary>
    public void ShowDialog()
    {
        if (Available == null) return;
        if (_dialog is { IsDisposed: false })
        {
            if (_dialog.WindowState == FormWindowState.Minimized) _dialog.WindowState = FormWindowState.Normal;
            _dialog.Activate();
            return;
        }
        _dialog = new UpdateDialog(this, Available);
        _dialog.FormClosed += (_, _) => _dialog = null;
        _dialog.Show();
        _dialog.Activate();
    }

    /// <summary>Called by the window once the new exe is in place and started.</summary>
    internal void QuitForRestart()
    {
        Core.Log.Info("Update installed, quitting for the restart");
        _quit();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _dialog?.Close();
    }
}
