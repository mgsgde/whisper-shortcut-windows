using System.Diagnostics;
using System.Windows.Forms;

namespace WhisperShortcut.UiTests;

/// <summary>Opens the real chat window off screen against a throwaway settings.json.</summary>
public sealed class ChatWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wsw-uitests-" + Guid.NewGuid().ToString("N"));

    public ChatWindowTests()
    {
        Directory.CreateDirectory(_dir);
        Core.Log.Init(Path.Combine(_dir, "test.log"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }


    /// <summary>The app builds the chat in advance, so the shortcut only has to make it visible.</summary>
    [Fact]
    public void PrewarmedChat_ShowsInstantly()
    {
        Sta.Run(() =>
        {
            using var form = NewChat();
            form.Prewarm();
            Assert.False(form.Visible);
            Assert.Equal(1.0, form.Opacity);

            form.Load += (_, _) => form.Location = new System.Drawing.Point(-3000, -3000); // keep the test off screen
            form.Location = new System.Drawing.Point(-3000, -3000);
            var watch = Stopwatch.StartNew();
            form.Show();
            Application.DoEvents();
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 600, $"prewarmed chat window took {watch.ElapsedMilliseconds} ms to show");
        });
    }

    /// <summary>Without the prebuilt window this is what the first chat shortcut costs.</summary>
    [Fact]
    public void Chat_OpensQuickly()
    {
        Sta.Run(() =>
        {
            using (var warm = NewChat()) { Place(warm); warm.Show(); Application.DoEvents(); } // JIT/font warm-up
            var watch = Stopwatch.StartNew();
            using var form = NewChat();
            Place(form);
            form.Show();
            Application.DoEvents();
            watch.Stop();
            const int budget = 3000;
            Assert.True(watch.ElapsedMilliseconds < budget, $"chat window took {watch.ElapsedMilliseconds} ms to open (budget {budget} ms)");
        });
    }

    private static void Place(ChatWindow form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-3000, -3000);
    }

    private ChatWindow NewChat()
    {
        var settings = Core.Settings.Load(Path.Combine(_dir, "settings.json"), new Core.PlainSecretProtector());
        return new ChatWindow(settings, new Core.ChatClient(() => "", () => ""), new OfflineEngine(settings), () => { }, () => false, () => { }, () => { })
        {
            ShowInTaskbar = false,
        };
    }
}
