using System.Diagnostics;
using System.Windows.Forms;

namespace WhisperShortcut.UiTests;

/// <summary>Drives the recording pill and the toast frame by frame; both log, hence the temp log.</summary>
public sealed class OverlayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wsw-uitests-" + Guid.NewGuid().ToString("N"));

    public OverlayTests()
    {
        Directory.CreateDirectory(_dir);
        Core.Log.Init(Path.Combine(_dir, "test.log"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }


    /// <summary>The pill repaints 30 times a second while Parakeet transcribes; its shadow is drawn once per size now.</summary>
    [Fact]
    public void ProcessingFrames_AreCheap()
    {
        Sta.Run(() =>
        {
            using var overlay = new RecordingOverlay();
            overlay.ShowProcessing();
            Application.DoEvents();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) overlay.TickForTests();
            watch.Stop();
            overlay.HideOverlay();
            const int budget = 1500;
            Assert.True(watch.ElapsedMilliseconds < budget, $"100 frames took {watch.ElapsedMilliseconds} ms ({watch.ElapsedMilliseconds / 100.0:F1} ms per frame, budget {budget} ms)");
        });
    }

    /// <summary>A warning stays 30 s and used to be repainted 30 times a second.</summary>
    [Fact]
    public void ToastAtRest_DoesNotRepaint()
    {
        Sta.Run(() =>
        {
            using var toast = new Toast();
            toast.Present("Test", "Ein Hinweis zum Testen.", ToolTipIcon.Warning, null, null);
            Application.DoEvents();
            Thread.Sleep(300); // the 0.18 s fade-in is over
            toast.TickForTests(); // draws the frame at rest
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 300; i++) toast.TickForTests();
            watch.Stop();
            toast.Dismiss();
            const int budget = 150;
            Assert.True(watch.ElapsedMilliseconds < budget, $"300 ticks at rest took {watch.ElapsedMilliseconds} ms (budget {budget} ms, nothing should be drawn)");
        });
    }
}
