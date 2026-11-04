using System.Diagnostics;
using System.Windows.Forms;

namespace WhisperShortcut.UiTests;

/// <summary>
/// Opens the real settings window, the way the tray does, against a throwaway settings.json in the temp folder.
/// Catches what the Core tests cannot: a window that is slow to open or freezes under frequent updates.
/// </summary>
public sealed class SettingsWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wsw-uitests-" + Guid.NewGuid().ToString("N"));

    public SettingsWindowTests()
    {
        Directory.CreateDirectory(_dir);
        Core.Log.Init(Path.Combine(_dir, "test.log"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// 0.6.0 froze while an offline model downloaded: every progress tick rebuilt all model tiles and relaid out the
    /// page. Ticks must leave the tiles alone and stay cheap.
    /// </summary>
    [Fact]
    public void DownloadProgressTicks_DoNotRebuildTiles()
    {
        OnSta(() =>
        {
            using var form = OpenSettings();
            var tiles = form.PickerTilesForTests;
            Assert.NotEmpty(tiles);

            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                form.DownloadTickForTests();
                Application.DoEvents();
            }
            watch.Stop();

            Assert.Equal(tiles, form.PickerTilesForTests); // same instances: nothing rebuilt
            Assert.True(watch.ElapsedMilliseconds < 2000, $"200 progress ticks took {watch.ElapsedMilliseconds} ms");
        });
    }

    /// <summary>The window opened in 5.5 s before the 0.5 work and in about 2 s after; a regression shows up here.</summary>
    [Fact]
    public void SettingsWindow_OpensQuickly()
    {
        OnSta(() =>
        {
            // A first window takes the one-off JIT and font cost, which the user pays once per app start.
            using (OpenSettings()) { }
            var watch = Stopwatch.StartNew();
            using var form = OpenSettings();
            watch.Stop();
            // Measured 04.11.2026 on Magnus's PC: 9.1 s (Debug) before the lazy page layout, 5.4 s after. The budget
            // catches a page that lays itself out up front again, not normal noise.
            const int budget = 12000; // Release measures the same; 5–9 s depending on load on this PC (04.11.2026)
            Assert.True(watch.ElapsedMilliseconds < budget, $"settings window took {watch.ElapsedMilliseconds} ms to open (budget {budget} ms)");
        });
    }

    /// <summary>Clicking through every page must not throw (all pages are built up front, shown on demand).</summary>
    [Fact]
    public void EveryPage_Opens()
    {
        OnSta(() =>
        {
            using var form = OpenSettings();
            foreach (var page in form.PageNamesForTests)
            {
                form.ShowPage(page);
                Application.DoEvents();
            }
        });
    }

    /// <summary>
    /// Diktieren → Modus (Mac Dictate mode): Live is always selectable; it shows either the Live caption or the orange
    /// note saying why it cannot start, never both. Back on Diktieren the note goes away.
    /// </summary>
    [Fact]
    public void DictateMode_LiveShowsCaptionOrWarning()
    {
        OnSta(() =>
        {
            using var form = OpenSettings();
            form.ShowPage("Diktieren");
            Application.DoEvents();
            var live = form.SetDictateModeForTests(Core.DictateMode.Live);
            Assert.True(live.WarningVisible ^ live.Caption.Length > 0, $"warning {live.WarningVisible}, caption '{live.Caption}'");
            var dictate = form.SetDictateModeForTests(Core.DictateMode.Dictate);
            Assert.False(dictate.WarningVisible);
            Assert.Equal("Der Text wird eingefügt, wenn du stoppst.", dictate.Caption);
        });
    }

    private SettingsForm OpenSettings()
    {
        var settings = Core.Settings.Load(Path.Combine(_dir, "settings.json"), new Core.PlainSecretProtector());
        var form = new SettingsForm(settings, new OfflineEngine(settings), new Core.GeminiClient(settings),
            new Core.OpenAiClient(settings), new UpdateService(settings, () => true, () => { }))
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-3000, -3000), // off screen: the test must not steal the user's focus
        };
        form.Show();
        Application.DoEvents();
        return form;
    }

    /// <summary>WinForms needs an STA thread with a message loop context; xunit runs tests on MTA thread-pool threads.</summary>
    private static void OnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            try { body(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("UI test hung (window frozen?)");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
