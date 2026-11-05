using System.Runtime.InteropServices;

namespace WhisperShortcut;

/// <summary>
/// Named events between a second start and the running instance: "show" opens the settings (Mac
/// applicationShouldHandleReopen) when someone starts the app from the Start menu while it runs; "quit" is what
/// <c>--uninstall</c> sends so the running instance exits before its files are removed.
/// </summary>
internal sealed class InstanceSignals : IDisposable
{
    public const string ShowName = @"Local\WhisperShortcut.Windows.Show";
    public const string QuitName = @"Local\WhisperShortcut.Windows.Quit";

    private readonly EventWaitHandle _show, _quit;
    private RegisteredWaitHandle? _showWait, _quitWait;

    /// <summary>
    /// Creates both events. Done right after the mutex is won and before the slow TrayApp constructor, so a second
    /// start that arrives during startup finds them; an auto-reset event stays set until the wait below picks it up.
    /// </summary>
    public InstanceSignals()
    {
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
    }

    /// <summary>Starts listening. The callbacks run on a thread-pool thread: marshal to the UI thread inside them.</summary>
    public void Listen(Action onShow, Action onQuit)
    {
        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => Run(onShow), null, Timeout.Infinite, executeOnlyOnce: false);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => Run(onQuit), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) { Core.Log.Error("Instance signal failed", ex); }
    }

    /// <summary>Sets the named event of the running instance. False when it does not exist or cannot be opened.</summary>
    public static bool Signal(string name)
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(name);
            return e.Set();
        }
        catch (Exception ex)
        {
            Core.Log.Error($"Signalling the running instance failed ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>
    /// Lets the running instance bring its window to the front: Windows only allows that for a process that was
    /// given the foreground right. -1 = ASFW_ANY.
    /// </summary>
    public static void AllowForeground()
    {
        try { AllowSetForegroundWindow(-1); } catch (Exception) { }
    }

    public void Dispose()
    {
        _showWait?.Unregister(null);
        _quitWait?.Unregister(null);
        _show.Dispose();
        _quit.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
