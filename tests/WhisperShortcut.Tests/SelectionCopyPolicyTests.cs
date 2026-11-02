using WhisperShortcut.Core;
using Xunit;

namespace WhisperShortcut.Tests;

public class SelectionCopyPolicyTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void CopyChanged_Unchanged_False() => Assert.False(SelectionCopyPolicy.CopyChanged(5, 5, 0));

    [Fact]
    public void CopyChanged_Moved_True() => Assert.True(SelectionCopyPolicy.CopyChanged(5, 6, 0));

    [Fact]
    public void CopyChanged_MovedToOwnWrite_False() => Assert.False(SelectionCopyPolicy.CopyChanged(5, 7, 7));

    [Fact]
    public void CopyChanged_MovedPastOwnWrite_True() => Assert.True(SelectionCopyPolicy.CopyChanged(5, 8, 7));

    [Fact]
    public void CopyChanged_NoOwnWrite_PlainCompare()
    {
        Assert.True(SelectionCopyPolicy.CopyChanged(0, 1, 0));
        Assert.False(SelectionCopyPolicy.CopyChanged(3, 3, 0));
    }

    [Fact]
    public void Poll_UnchangedBeforeDeadline_Wait()
        => Assert.Equal(CopyPollResult.Wait, SelectionCopyPolicy.Poll(false, false, Ms(1999), null));

    [Fact]
    public void Poll_UnchangedAtDeadline_NoSelection()
        => Assert.Equal(CopyPollResult.NoSelection, SelectionCopyPolicy.Poll(false, false, Ms(2000), null));

    [Fact]
    public void Poll_ChangedReadable_Selection()
        => Assert.Equal(CopyPollResult.Selection, SelectionCopyPolicy.Poll(true, true, Ms(30), Ms(30)));

    [Fact]
    public void Poll_ChangedReadableAfterDeadline_Selection()
        => Assert.Equal(CopyPollResult.Selection, SelectionCopyPolicy.Poll(true, true, Ms(2300), Ms(2200)));

    [Fact]
    public void Poll_ChangedUnreadableEarly_Wait()
        => Assert.Equal(CopyPollResult.Wait, SelectionCopyPolicy.Poll(true, false, Ms(100), Ms(50)));

    [Fact]
    public void Poll_ChangedLateUnreadableInGrace_Wait()
        => Assert.Equal(CopyPollResult.Wait, SelectionCopyPolicy.Poll(true, false, Ms(2100), Ms(1900)));

    [Fact]
    public void Poll_ChangedLateUnreadablePastGrace_Unreadable()
        => Assert.Equal(CopyPollResult.Unreadable, SelectionCopyPolicy.Poll(true, false, Ms(2400), Ms(1900)));

    [Fact]
    public void Poll_ChangedEarlyUnreadableAtDeadline_Unreadable()
        => Assert.Equal(CopyPollResult.Unreadable, SelectionCopyPolicy.Poll(true, false, Ms(2000), Ms(100)));
}
