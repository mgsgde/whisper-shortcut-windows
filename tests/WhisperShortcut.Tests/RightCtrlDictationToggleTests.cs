using WhisperShortcut.Core;
using static WhisperShortcut.Core.RightCtrlDictationToggle;

namespace WhisperShortcut.Tests;

public class RightCtrlDictationToggleTests
{
    private sealed class FakeDictation : IRightCtrlDictationDelegate
    {
        public bool StartSucceeds = true;
        public bool Recording;
        public bool Processing;
        public int Starts, Stops, Discards, Cancels;

        public bool Start()
        {
            Starts++;
            if (StartSucceeds) Recording = true;
            return StartSucceeds;
        }

        public void Stop() { Stops++; Recording = false; Processing = true; }
        public void Discard() { Discards++; Recording = false; }
        public bool IsRecording() => Recording;
        public bool IsProcessing() => Processing;
        public void CancelProcessing() { Cancels++; Processing = false; }
    }

    private readonly FakeDictation _d = new();
    private long _now = 10_000;
    private readonly RightCtrlDictationToggle _t;

    public RightCtrlDictationToggleTests() => _t = new RightCtrlDictationToggle(_d, () => _now);

    private void Tap() { _t.KeyDown(); _t.KeyUp(); }

    [Fact]
    public void TapStartsRecording_OnKeyDown_ConfirmedOnRelease()
    {
        _t.KeyDown();
        Assert.Equal(1, _d.Starts);
        Assert.Equal(State.Starting, _t.Current);
        Assert.True(_t.WatchingOtherKeys);
        _t.KeyUp();
        Assert.Equal(State.Recording, _t.Current);
        Assert.False(_t.WatchingOtherKeys);
        Assert.Equal(0, _d.Stops);
    }

    [Fact]
    public void LongHold_StillToggles_NoHoldToTalk()
    {
        _t.KeyDown();
        _now += 5000;
        _t.KeyUp();
        Assert.Equal(State.Recording, _t.Current);
        Assert.True(_d.Recording);
    }

    [Fact]
    public void SecondTapStops_OnRelease()
    {
        Tap();
        _t.KeyDown();
        Assert.Equal(State.Stopping, _t.Current);
        Assert.Equal(0, _d.Stops);
        _t.KeyUp();
        Assert.Equal(1, _d.Stops);
        Assert.Equal(State.Idle, _t.Current);
    }

    [Fact]
    public void FailedStart_StaysIdle()
    {
        _d.StartSucceeds = false;
        Tap();
        Assert.Equal(1, _d.Starts);
        Assert.Equal(State.Idle, _t.Current);
        Assert.False(_t.WatchingOtherKeys);
    }

    [Fact]
    public void OtherKeyDuringStart_DiscardsRecording()
    {
        _t.KeyDown();
        _t.OtherKeyDown(); // Strg+C
        Assert.Equal(1, _d.Discards);
        Assert.Equal(State.Idle, _t.Current);
        _t.KeyUp();
        Assert.Equal(State.Idle, _t.Current);
        Assert.Equal(0, _d.Stops);
    }

    [Fact]
    public void OtherKeyDuringStop_KeepsRecording()
    {
        Tap();
        _t.KeyDown();
        _t.OtherKeyDown();
        Assert.Equal(State.Recording, _t.Current);
        _t.KeyUp();
        Assert.Equal(0, _d.Stops);
        Assert.Equal(0, _d.Discards);
        Assert.True(_d.Recording);
        Tap(); // the next real tap still stops
        Assert.Equal(1, _d.Stops);
    }

    [Fact]
    public void OtherKey_WhileIdleOrRecording_IsIgnored()
    {
        _t.OtherKeyDown();
        Assert.Equal(State.Idle, _t.Current);
        Tap();
        _t.OtherKeyDown();
        Assert.Equal(State.Recording, _t.Current);
        Assert.Equal(0, _d.Discards);
    }

    [Fact]
    public void TapDuringProcessing_Cancels_OnRelease()
    {
        _d.Processing = true;
        _t.KeyDown();
        Assert.Equal(State.Cancelling, _t.Current);
        Assert.Equal(0, _d.Cancels);
        _t.KeyUp();
        Assert.Equal(1, _d.Cancels);
        Assert.Equal(0, _d.Starts);
        Assert.Equal(State.Idle, _t.Current);
    }

    [Fact]
    public void CancelGuard_TapWithinTwoSecondsOfStop_IsIgnored()
    {
        Tap();
        Tap(); // stop → processing
        _now += 1999;
        _t.KeyDown();
        Assert.Equal(State.Idle, _t.Current);
        _t.KeyUp();
        Assert.Equal(0, _d.Cancels);
        Assert.Equal(1, _d.Starts);
        Assert.True(_d.Processing);
    }

    [Fact]
    public void CancelGuard_AfterTwoSeconds_Cancels()
    {
        Tap();
        Tap();
        _now += RightCtrlDictationToggle.CancelGuardWindowMs;
        Tap();
        Assert.Equal(1, _d.Cancels);
        Assert.Equal(State.Idle, _t.Current);
    }

    [Fact]
    public void CancelGuard_OnlyAfterOwnStop_ProcessingFromElsewhereCancelsAtOnce()
    {
        _d.Processing = true; // started by Alt+1, not by right Ctrl
        Tap();
        Assert.Equal(1, _d.Cancels);
    }

    [Fact]
    public void RecordingStoppedElsewhere_NextTapStartsFresh()
    {
        Tap();
        _d.Recording = false; // stopped from the tray
        _t.KeyDown();
        Assert.Equal(2, _d.Starts);
        Assert.Equal(State.Starting, _t.Current);
        _t.KeyUp();
        Assert.Equal(State.Recording, _t.Current);
        Assert.Equal(0, _d.Stops);
    }

    [Fact]
    public void RecordingStoppedElsewhere_WhileProcessing_TapCancels()
    {
        Tap();
        _d.Recording = false;
        _d.Processing = true; // stopped elsewhere, no guard stamp from this toggle
        Tap();
        Assert.Equal(1, _d.Cancels);
        Assert.Equal(1, _d.Starts);
    }

    [Fact]
    public void OtherKeyDuringCancelling_KeepsTranscription()
    {
        _d.Processing = true;
        _t.KeyDown();
        _t.OtherKeyDown();
        Assert.Equal(State.Idle, _t.Current);
        _t.KeyUp();
        Assert.Equal(0, _d.Cancels);
        Assert.True(_d.Processing);
    }

    [Fact]
    public void KeyRepeat_IsNoOp_InEveryHeldState()
    {
        _t.KeyDown();
        _t.KeyDown();
        _t.KeyDown();
        Assert.Equal(1, _d.Starts);
        Assert.Equal(State.Starting, _t.Current);
        _t.KeyUp();

        _t.KeyDown();
        _t.KeyDown();
        Assert.Equal(State.Stopping, _t.Current);
        _t.KeyUp();
        Assert.Equal(1, _d.Stops);

        _now += 5000;
        _t.KeyDown();
        _t.KeyDown();
        Assert.Equal(State.Cancelling, _t.Current);
        _t.KeyUp();
        Assert.Equal(1, _d.Cancels);
        Assert.Equal(1, _d.Starts);
    }

    [Fact]
    public void StrayKeyUp_IsIgnored()
    {
        _t.KeyUp();
        Assert.Equal(State.Idle, _t.Current);
        Tap();
        _t.KeyUp();
        Assert.Equal(State.Recording, _t.Current);
        Assert.Equal(0, _d.Stops);
    }

    [Fact]
    public void Disabled_KeyDownDoesNothing()
    {
        _t.Enabled = false;
        Tap();
        Assert.Equal(0, _d.Starts);
        _d.Processing = true;
        Tap();
        Assert.Equal(0, _d.Cancels);
        Assert.Equal(State.Idle, _t.Current);
    }

    [Fact]
    public void DisabledMidPress_ReleaseStillResolves()
    {
        _t.KeyDown();
        _t.Enabled = false; // like the Mac: only key down checks the setting
        _t.KeyUp();
        Assert.Equal(State.Recording, _t.Current);
        _t.KeyDown();
        Assert.Equal(State.Recording, _t.Current);
        Assert.Equal(0, _d.Stops);
    }
}
