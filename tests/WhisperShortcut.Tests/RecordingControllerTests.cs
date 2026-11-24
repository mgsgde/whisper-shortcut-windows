using WhisperShortcut.Core;
using Xunit;

namespace WhisperShortcut.Tests;

public class RecordingControllerTests
{
    private sealed class FakeTake : IRecordingTake
    {
        public TakePhase Phase { get; set; } = TakePhase.Recording;
        public long Serial { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public int Disposed;
        public void Dispose() => Disposed++;
    }

    private static (RecordingController<FakeTake> C, List<string> Events) New()
    {
        var c = new RecordingController<FakeTake>();
        var events = new List<string>();
        c.PhaseChanged += () => events.Add(c.PhaseName);
        return (c, events);
    }

    [Fact]
    public async Task StartThenStop_GoesRecordingProcessingIdle()
    {
        var (c, events) = New();
        var take = new FakeTake();
        Assert.True(c.IsIdle);
        Assert.True(c.Start(take));
        Assert.True(c.IsRecording);
        Assert.Equal(1, take.Serial);

        var seenInProcess = "";
        await c.StopAsync(t => { seenInProcess = c.PhaseName; return Task.CompletedTask; });
        Assert.Equal("Processing", seenInProcess); // already Processing when the process step starts
        Assert.True(c.IsProcessing);

        c.End(take);
        Assert.True(c.IsIdle);
        Assert.Equal(1, take.Disposed);
        Assert.Equal(new[] { "Recording", "Processing", "Idle" }, events);
    }

    [Fact]
    public void StartThenCancel_DisposesTheTakeAndGoesIdle()
    {
        var (c, events) = New();
        var take = new FakeTake();
        c.Start(take);
        Assert.Equal(CancelOutcome.RecordingDiscarded, c.Cancel());
        Assert.True(c.IsIdle);
        Assert.Equal(1, take.Disposed);
        Assert.Equal(new[] { "Recording", "Idle" }, events);
    }

    [Fact]
    public async Task CancelWhileProcessing_CancelsTheRequestOnly()
    {
        var (c, _) = New();
        var take = new FakeTake { Cts = new CancellationTokenSource() };
        c.Start(take);
        await c.StopAsync(_ => Task.CompletedTask);
        Assert.Equal(CancelOutcome.RequestCancelled, c.Cancel());
        Assert.True(take.Cts.IsCancellationRequested);
        Assert.True(c.IsProcessing); // the run ends itself via End
        Assert.Equal(0, take.Disposed);
    }

    [Fact]
    public async Task DoubleStop_ProcessesOnce()
    {
        var (c, _) = New();
        c.Start(new FakeTake());
        int runs = 0;
        await c.StopAsync(_ => { runs++; return Task.CompletedTask; });
        await c.StopAsync(_ => { runs++; return Task.CompletedTask; });
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task StopWhileNotRecording_DoesNothing()
    {
        var (c, events) = New();
        int runs = 0;
        await c.StopAsync(_ => { runs++; return Task.CompletedTask; });
        Assert.Equal(0, runs);
        Assert.Equal(CancelOutcome.None, c.Cancel());
        Assert.Empty(events);
    }

    [Fact]
    public void StartWhileBusy_IsRefusedAndTouchesNothing()
    {
        var (c, _) = New();
        var first = new FakeTake();
        c.Start(first);
        var second = new FakeTake();
        Assert.False(c.Start(second));
        Assert.Same(first, c.Current);
        Assert.Equal(0, second.Serial);
    }

    [Fact]
    public void SerialsCountUp_AndAnEarlierTakeIsNotRunning()
    {
        var (c, _) = New();
        var a = new FakeTake(); c.Start(a); c.End(a);
        var b = new FakeTake(); c.Start(b);
        Assert.Equal(2, b.Serial);
        Assert.False(c.IsRunning(a));
        Assert.True(c.IsRunning(b));
    }

    [Fact]
    public void ReplayEntersProcessingFromIdle()
    {
        var (c, events) = New();
        var replay = new FakeTake { Phase = TakePhase.Processing };
        Assert.True(c.Attach(replay));
        Assert.True(c.IsProcessing);
        Assert.False(c.BeginProcessing(replay)); // nothing left to announce
        c.End(replay);
        Assert.Equal(new[] { "Processing", "Idle" }, events);
    }

    [Fact]
    public void RaiseFalse_StaysSilent_ForCallersThatRefreshInTheirOwnOrder()
    {
        var (c, events) = New();
        var take = new FakeTake();
        c.Start(take, raise: false);
        c.End(take, raise: false);
        Assert.Empty(events);
        Assert.True(c.IsIdle);
    }
}

public class KeptAudioTests
{
    private sealed class Clock { public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc); }

    private static (KeptAudio K, Clock T, List<KeptItem> Dropped) New()
    {
        var t = new Clock();
        var k = new KeptAudio(() => t.Now);
        var dropped = new List<KeptItem>();
        k.Expired += dropped.Add;
        return (k, t, dropped);
    }

    [Fact]
    public void TemplateExpiresOnce_AfterTenMinutes()
    {
        var (k, t, dropped) = New();
        k.KeepTemplate(new byte[] { 1 }, 2, "Praxisnotiz");
        t.Now += TemplateReplay.Lifetime - TimeSpan.FromSeconds(1);
        k.ExpireDue();
        Assert.Empty(dropped);
        Assert.True(k.NeedsTimer);

        t.Now += TimeSpan.FromSeconds(2);
        k.ExpireDue();
        k.ExpireDue();
        Assert.Null(k.CurrentTemplate());
        Assert.Equal(new[] { KeptItem.TemplateReplay }, dropped); // exactly once, however often it is asked
        Assert.False(k.NeedsTimer);
    }

    [Fact]
    public void RetryWithinTheWindow_KeepsTheFile()
    {
        var (k, t, dropped) = New();
        k.KeepTemplate(new byte[] { 7, 8 }, 3.5, "  Vorlage X ");
        t.Now += TimeSpan.FromMinutes(9);
        var first = k.CurrentTemplate();
        Assert.NotNull(first);
        Assert.Equal(new byte[] { 7, 8 }, first!.Value.Wav);
        Assert.Equal("Vorlage X", first.Value.TemplateName);
        t.Now += TimeSpan.FromMinutes(0.5); // 9.5 min after keeping: still there for a second retry
        Assert.NotNull(k.CurrentTemplate());
        Assert.Empty(dropped);
    }

    [Fact]
    public void ReadingAnExpiredItem_RaisesExpiredToo()
    {
        var (k, t, dropped) = New();
        k.KeepTemplate(new byte[] { 1 }, 1, "V");
        t.Now += TemplateReplay.Lifetime + TimeSpan.FromSeconds(1);
        Assert.Null(k.CurrentTemplate()); // no timer tick needed: one owner decides, and says so once
        Assert.Equal(new[] { KeptItem.TemplateReplay }, dropped);
    }

    [Fact]
    public void ScreenshotExpiresAfterFiveMinutes_AndIsUsedOnce()
    {
        var (k, t, dropped) = New();
        var img = new ScreenshotImage(new byte[] { 1, 2, 3 }, "image/png");
        k.HoldShot(img);
        Assert.True(k.HasShot);
        Assert.Same(img, k.TakeShot());
        Assert.Null(k.TakeShot());

        k.HoldShot(img);
        t.Now += ScreenshotHold.Lifetime + TimeSpan.FromSeconds(1);
        Assert.Null(k.TakeShot());
        Assert.Equal(new[] { KeptItem.Screenshot }, dropped);
    }

    [Fact]
    public void ExplicitClearsAndCancelledAreNotExpiry()
    {
        var (k, t, dropped) = New();
        k.KeepTemplate(new byte[] { 1 }, 1, "V");
        k.ClearTemplate();
        k.KeepCancelled(new byte[] { 1 }, 1);
        t.Now += TimeSpan.FromHours(1);
        k.ExpireDue();
        Assert.Empty(dropped);
        Assert.True(k.HasCancelled); // an undo for the cancel just made: only use ends it
        Assert.NotNull(k.TakeCancelled());
        Assert.False(k.HasCancelled);
    }

    [Fact]
    public void LastFailedHasNoTimeLimit()
    {
        var (k, t, _) = New();
        k.LastFailed = new FailedRun(new byte[] { 1 }, RecordingKindName.Dictate, 1);
        t.Now += TimeSpan.FromDays(1);
        k.ExpireDue();
        Assert.NotNull(k.LastFailed);
    }
}
