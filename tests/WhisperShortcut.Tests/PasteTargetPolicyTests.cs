using WhisperShortcut.Core;
using Xunit;

namespace WhisperShortcut.Tests;

public class PasteTargetPolicyTests
{
    private static PasteTargetAction D(bool hasTarget = true, bool own = false, bool exists = true,
        bool minimised = false, bool fgIsTarget = false, ForegroundKind fg = ForegroundKind.None)
        => PasteTargetPolicy.Decide(hasTarget, own, exists, minimised, fgIsTarget, fg);

    [Fact]
    public void NoTarget_OwnForeground_ClipboardOnlyOwnWindow()
        => Assert.Equal(PasteTargetAction.ClipboardOnlyOwnWindow, D(hasTarget: false, fg: ForegroundKind.Own));

    [Theory]
    [InlineData(ForegroundKind.None)]
    [InlineData(ForegroundKind.Shell)]
    [InlineData(ForegroundKind.Foreign)]
    public void NoTarget_OtherForeground_PasteNow(ForegroundKind fg)
        => Assert.Equal(PasteTargetAction.PasteNow, D(hasTarget: false, fg: fg));

    [Fact]
    public void OwnTarget_ClipboardOnlyOwnWindow()
        => Assert.Equal(PasteTargetAction.ClipboardOnlyOwnWindow, D(own: true, fgIsTarget: true));

    [Fact]
    public void TargetGone_ClipboardOnlyTargetGone()
        => Assert.Equal(PasteTargetAction.ClipboardOnlyTargetGone, D(exists: false, fg: ForegroundKind.Shell));

    [Fact]
    public void ForegroundIsTarget_PasteNow()
        => Assert.Equal(PasteTargetAction.PasteNow, D(fgIsTarget: true, fg: ForegroundKind.Foreign));

    [Fact]
    public void ForeignForeground_ClipboardOnlyTargetChanged()
        => Assert.Equal(PasteTargetAction.ClipboardOnlyTargetChanged, D(fg: ForegroundKind.Foreign));

    [Fact]
    public void Minimised_ClipboardOnlyTargetChanged()
        => Assert.Equal(PasteTargetAction.ClipboardOnlyTargetChanged, D(minimised: true, fg: ForegroundKind.None));

    [Theory]
    [InlineData(ForegroundKind.None)]
    [InlineData(ForegroundKind.Own)]
    [InlineData(ForegroundKind.Shell)]
    public void NothingForeign_ActivateTargetThenPaste(ForegroundKind fg)
        => Assert.Equal(PasteTargetAction.ActivateTargetThenPaste, D(fg: fg));

    [Fact]
    public void MinimisedWithShellInFront_ClipboardOnlyTargetChanged()
        => Assert.Equal(PasteTargetAction.ClipboardOnlyTargetChanged, D(minimised: true, fg: ForegroundKind.Shell));

    [Theory]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("NotifyIconOverflowWindow")]
    [InlineData("TopLevelWindowForOverflowXamlIsland")]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    public void ShellClass_True(string name) => Assert.True(PasteTargetPolicy.IsShellWindowClass(name));

    [Theory]
    [InlineData("Notepad")]
    [InlineData("OpusApp")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("shell_traywnd")]
    public void OtherClass_False(string? name) => Assert.False(PasteTargetPolicy.IsShellWindowClass(name));
}

public class ClipboardPasteGuardTests
{
    [Fact]
    public void UnchangedPastes() =>
        Assert.True(ClipboardPasteGuard.PasteAllowed(7, 7, "neu", "neu"));

    [Fact]
    public void MovedSequenceBlocks() =>
        Assert.False(ClipboardPasteGuard.PasteAllowed(7, 8, "neu", "neu"));

    [Fact]
    public void OtherTextBlocks()
    {
        Assert.False(ClipboardPasteGuard.PasteAllowed(7, 7, "neu", "alt"));
        Assert.False(ClipboardPasteGuard.PasteAllowed(7, 7, "neu", null));
    }

    [Fact]
    public void RestoreDecision()
    {
        Assert.True(ClipboardPasteGuard.RestoreAllowed(7, 7));
        Assert.False(ClipboardPasteGuard.RestoreAllowed(7, 9));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void MayAutoPaste(bool setting, bool clipboardOnlyRun, bool expected) =>
        Assert.Equal(expected, ClipboardPasteGuard.MayAutoPaste(setting, clipboardOnlyRun));

    [Fact]
    public void Constants()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(150), ClipboardPasteGuard.PrePasteDelay);
        Assert.Equal(TimeSpan.FromSeconds(3), ClipboardPasteGuard.RestoreDelay);
    }
}
