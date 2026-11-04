using System.Diagnostics;
using System.Windows.Forms;

namespace WhisperShortcut.UiTests;

/// <summary>Opens the real welcome tour off screen against a throwaway settings.json.</summary>
public sealed class WelcomeWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wsw-uitests-" + Guid.NewGuid().ToString("N"));

    public WelcomeWindowTests()
    {
        Directory.CreateDirectory(_dir);
        Core.Log.Init(Path.Combine(_dir, "test.log"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }


    /// <summary>Page switches and the first Show used to lay out every nested auto-size table one by one.</summary>
    [Fact]
    public void WelcomeTour_OpensQuickly()
    {
        Sta.Run(() =>
        {
            using (OpenTour()) { } // JIT/font warm-up, paid once per app start
            var watch = Stopwatch.StartNew();
            using var form = OpenTour();
            watch.Stop();
            const int budget = 3000;
            Assert.True(watch.ElapsedMilliseconds < budget, $"welcome tour took {watch.ElapsedMilliseconds} ms to open (budget {budget} ms)");
        });
    }

    /// <summary>FitWraps used to relay out all pages on every Resize.</summary>
    [Fact]
    public void Resizing_DoesNotRelayoutEveryPage()
    {
        Sta.Run(() =>
        {
            using var form = OpenTour();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++)
            {
                form.Width += 10;
                Application.DoEvents();
            }
            watch.Stop();
            // A width change rewraps the visible page (about 100 ms each on Magnus's PC, up to 2.3 s for 20 under the
            // load of the full UI test run); the budget catches every page relaying out again, not that.
            const int budget = 4000;
            Assert.True(watch.ElapsedMilliseconds < budget, $"20 resizes took {watch.ElapsedMilliseconds} ms (budget {budget} ms)");
        });
    }

    private WelcomeForm OpenTour()
    {
        var settings = Core.Settings.Load(Path.Combine(_dir, "settings.json"), new Core.PlainSecretProtector());
        var form = new WelcomeForm(settings, new OfflineEngine(settings), new Core.GeminiClient(settings), new Core.OpenAiClient(settings))
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-3000, -3000), // off screen: the test must not steal the user's focus
        };
        form.Show();
        Application.DoEvents();
        return form;
    }
}
