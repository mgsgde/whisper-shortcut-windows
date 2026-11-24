using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class LateCopyWatchTests
{
    [Fact]
    public void EachRecordingOwnsItsWatch_CancellingAnotherLeavesItAlone()
    {
        var recording = new LateCopyWatch();
        var token = recording.Start(10);
        recording.MarkArrived();

        var other = new LateCopyWatch(); // e.g. a later screenshot or another take
        other.Start(20);
        other.Cancel();

        Assert.False(token.IsCancellationRequested);
        Assert.True(recording.IsActive);
        Assert.True(recording.Arrived);
    }

    [Fact]
    public void Cancel_StopsTheWatchAndClearsTheFlag()
    {
        var w = new LateCopyWatch();
        var token = w.Start(1);
        w.MarkArrived();
        w.Cancel();
        Assert.True(token.IsCancellationRequested);
        Assert.False(w.IsActive);
        Assert.False(w.Arrived);
    }

    [Fact]
    public void Finish_KeepsTheArrivedFlag_AndRebaselineMovesOnlyAnActiveWatch()
    {
        var w = new LateCopyWatch();
        var token = w.Start(5);
        w.Rebaseline(9);
        Assert.Equal(9u, w.Baseline);
        w.MarkArrived();
        w.Finish(token);
        Assert.False(w.IsActive);
        Assert.True(w.Arrived);
        w.Rebaseline(99);
        Assert.Equal(9u, w.Baseline);
    }
}
