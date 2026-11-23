using WhisperShortcut.TestSupport;
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
            var budget = TimingBudget.Ms(1500);
            Assert.True(watch.ElapsedMilliseconds < budget, $"100 frames took {watch.ElapsedMilliseconds} ms ({watch.ElapsedMilliseconds / 100.0:F1} ms per frame, budget {budget} ms)");
        });
    }

    /// <summary>
    /// Every state of the Mac-style pill and card draws something opaque and keeps its ✕/✓ inside the window.
    /// With WS_OVERLAY_SHOTS set to a folder, the frames are saved there as PNGs (the Mac/Windows comparison in the PR).
    /// </summary>
    [Fact]
    public void AllStates_Render()
    {
        var shots = Environment.GetEnvironmentVariable("WS_OVERLAY_SHOTS");
        Sta.Run(() =>
        {
            using var overlay = new RecordingOverlay();
            var quick = Core.QuickActionCard.Select(Core.QuickActionList.Defaults, new Dictionary<string, int>());
            float level = 0.6f;
            var states = new (string Name, Action Show)[]
            {
                ("1-diktat", () => overlay.ShowRecording(Mode.Dictate, "Alt+1", TimeSpan.FromMinutes(10), () => level, () => 0.5f)),
                ("2-prompt-liest", () => overlay.ShowRecording(Mode.Prompt, "Alt+2", TimeSpan.FromMinutes(10), () => level, () => 0.5f, quick)),
                ("3-prompt-auswahl", () => overlay.SetSelection("Sehr geehrte Damen und Herren, anbei sende ich Ihnen die Unterlagen für das Projekt.")),
                ("4-prompt-nichts", () => { overlay.ShowRecording(Mode.Prompt, "Alt+2", TimeSpan.FromMinutes(10), () => level, () => 0.5f, quick, screenshot: true); overlay.SetSelection(null); }),
                ("5-vorlage", () => { overlay.ShowRecording(Mode.Template, "Alt+3", TimeSpan.FromMinutes(10), () => level, () => 0.5f, templateName: "Praxisnotiz"); overlay.SetSelection(null); }),
                ("6-verarbeitung", () => overlay.ShowProcessing(Core.UserCopy.RecognizingInstruction)),
                ("7-fertig", () => overlay.ShowDone("Eingefügt")),
                ("8-warnung", () => overlay.ShowWarning(new[] { "Ibuprofen", "600 mg", "dreimal täglich" }, pasted: true)),
            };
            foreach (var (name, show) in states)
            {
                show();
                Application.DoEvents();
                Thread.Sleep(250); // the fade-in and the check mark's pop are over
                for (int i = 0; i < 12; i++) overlay.TickForTests();
                using var frame = overlay.RenderForTests();
                var centre = frame.GetPixel(frame.Width / 2, frame.Height / 2);
                Assert.True(centre.A > 200, $"{name}: the centre of the frame is not opaque (alpha {centre.A})");
                if (!string.IsNullOrEmpty(shots))
                {
                    Directory.CreateDirectory(shots);
                    frame.Save(Path.Combine(shots, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            overlay.HideOverlay();
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
            var budget = TimingBudget.Ms(150);
            Assert.True(watch.ElapsedMilliseconds < budget, $"300 ticks at rest took {watch.ElapsedMilliseconds} ms (budget {budget} ms, nothing should be drawn)");
        });
    }
}
