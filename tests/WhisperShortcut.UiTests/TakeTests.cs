using System.Windows.Forms;

namespace WhisperShortcut.UiTests;

/// <summary>One run (Take): its teardown, the replay snapshot and the live run it owns. No window, no microphone.</summary>
public sealed class TakeTests
{
    private static Take NewTake(Core.RecordingKindName kind, LiveRun? live = null) =>
        new(kind, "Ctrl+Shift+D", new ClipboardPaster(IntPtr.Zero)) { Live = live };

    private static LiveRun NewLive(SynchronizationContext ui) =>
        new(ui, Core.PunctuationMode.Automatic, "app", Core.CaretContext.Unknown,
            (_, _, _) => Task.FromResult("x"), (_, _) => Task.FromResult(true));

    [Fact]
    public void Dispose_cancels_the_request_clears_what_the_take_holds_and_is_idempotent()
    {
        Sta.Run(() =>
        {
            var take = NewTake(Core.RecordingKindName.Prompt);
            take.Cts = new CancellationTokenSource();
            var token = take.Cts.Token;
            take.Shot = new Core.ScreenshotImage(new byte[] { 1 }, "image/png");
            take.Dispose();
            Assert.True(token.IsCancellationRequested);
            Assert.Null(take.Cts);
            Assert.Null(take.Audio);
            Assert.Null(take.Selection);
            Assert.Null(take.Shot);
            take.Dispose();
        });
    }

    [Fact]
    public void Dispose_discards_the_live_run_and_cancels_a_session_that_did_not_finish()
    {
        Sta.Run(() =>
        {
            var live = NewLive(new WindowsFormsSynchronizationContext());
            var take = NewTake(Core.RecordingKindName.Live, live);
            take.Dispose();
            Assert.True(live.Session.IsCancelled);
            live.Feed(new byte[3200], 3200); // a late buffer of the capture thread after the end: ignored
            live.Dispose();
        });
    }

    [Fact]
    public void A_finished_live_session_is_not_cancelled_by_the_end_of_the_take()
    {
        Sta.Run(() =>
        {
            var live = NewLive(new WindowsFormsSynchronizationContext());
            var wav = Core.Wav.Encode16(new short[16000], 16000);
            var outcome = live.FinishAsync(wav).GetAwaiter().GetResult();
            Assert.Equal(0, outcome.FailedPieces);
            NewTake(Core.RecordingKindName.Live, live).Dispose();
            Assert.False(live.Session.IsCancelled);
        });
    }

    [Fact]
    public void A_live_take_replays_as_a_plain_dictation()
    {
        var take = NewTake(Core.RecordingKindName.Live);
        take.PasteTarget = new IntPtr(42);
        var run = take.ToReplay(new byte[] { 1, 2 }, 1.5, null);
        Assert.Equal(Core.RecordingKindName.Dictate, run.Kind);
        Assert.Equal("Ctrl+Shift+D", run.StopKey);
        Assert.Equal(new IntPtr(42), run.Target);
        Assert.Equal(Mode.Dictate, take.Mode);
    }

    [Fact]
    public void A_replay_take_is_processing_and_clipboard_only_and_keeps_what_the_run_had()
    {
        var template = new Core.DictationTemplate { Id = "t1", Name = "Befund", Instructions = "x" };
        var run = new FailedRun(new byte[] { 9 }, Core.RecordingKindName.Template, 3, "Ctrl+Shift+T", "sel", "kürzen", new IntPtr(7), template,
            new Core.ScreenshotImage(new byte[] { 1 }, "image/png"), NoNewTextTemplate: true);
        var replay = Take.Replay(run, new ClipboardPaster(IntPtr.Zero), shotAllowed: false);
        Assert.Equal(TakePhase.Processing, replay.Phase);
        Assert.True(replay.ClipboardOnly);
        Assert.Null(replay.Shot); // screenshots were switched off since
        Assert.Equal("kürzen", replay.QuickAction);
        Assert.Same(template, replay.Template);
        Assert.True(replay.NoNewTextTemplate);
        Assert.Equal(Mode.Template, replay.Mode);
        Assert.Equal("Template:t1", replay.CheckpointMode);
        Assert.Equal("Live", NewTake(Core.RecordingKindName.Live).CheckpointMode);
        Assert.Equal("Prompt", NewTake(Core.RecordingKindName.Prompt).CheckpointMode);
    }
}
