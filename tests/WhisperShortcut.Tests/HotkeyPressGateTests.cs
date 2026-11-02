using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class HotkeyPressGateTests
{
    [Fact]
    public void UnarmedGateAcceptsEverything() => Assert.True(new HotkeyPressGate().Accept(0));

    [Fact]
    public void HeldKeyIsNeverAccepted()
    {
        var gate = new HotkeyPressGate();
        gate.Armed(1000);
        Assert.False(gate.Accept(1000));
        Assert.False(gate.Accept(1500));
        Assert.False(gate.Accept(2500));
    }

    [Fact]
    public void ReleasedKeyNeedsThreeHundredMilliseconds()
    {
        var gate = new HotkeyPressGate();
        gate.Armed(1000);
        gate.KeyUp(); // at t0 + 100
        Assert.False(gate.Accept(1200));
        Assert.False(gate.Accept(1299));
        Assert.True(gate.Accept(1300));
        Assert.True(gate.Accept(1400));
    }

    [Fact]
    public void ArmingAgainStartsOver()
    {
        var gate = new HotkeyPressGate();
        gate.Armed(0);
        gate.KeyUp();
        Assert.True(gate.Accept(1000));
        gate.Armed(1000);
        Assert.False(gate.Accept(1400));
        gate.KeyUp();
        Assert.False(gate.Accept(1200));
        Assert.True(gate.Accept(1400));
    }

    [Fact]
    public void ReleasedTracksKeyState()
    {
        var gate = new HotkeyPressGate();
        Assert.True(gate.Released);
        gate.Armed(0);
        Assert.False(gate.Released);
        Assert.False(gate.Accept(500));
        gate.KeyUp();
        Assert.True(gate.Released);
        Assert.False(gate.Accept(299));
        Assert.True(gate.Accept(300));
    }
}

public class HotkeyPolicyTests
{
    [Theory]
    [InlineData(false, true, true)]   // no settings window
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]   // settings in front, idle: the hotkey box records the keys
    [InlineData(true, false, true)]   // recording or processing keeps them
    public void WantGlobalHotkeys(bool settingsInFront, bool idle, bool expected) =>
        Assert.Equal(expected, HotkeyPolicy.WantGlobalHotkeys(settingsInFront, idle));
}

public class RecordingHotkeyPolicyTests
{
    [Fact]
    public void Idle_AlwaysStarts()
    {
        Assert.Equal(RecordingHotkeyDecision.Start, RecordingHotkeyPolicy.Decide(false, false, sameMode: false, gateOpen: false));
        Assert.Equal(RecordingHotkeyDecision.Start, RecordingHotkeyPolicy.Decide(false, false, sameMode: true, gateOpen: true));
    }

    [Fact]
    public void Recording_SameMode_StopsOnlyWhenGateOpen()
    {
        Assert.Equal(RecordingHotkeyDecision.Stop, RecordingHotkeyPolicy.Decide(true, false, sameMode: true, gateOpen: true));
        Assert.Equal(RecordingHotkeyDecision.Ignore, RecordingHotkeyPolicy.Decide(true, false, sameMode: true, gateOpen: false));
    }

    [Fact]
    public void Recording_OtherMode_IsIgnoredEvenWhenGateOpen() =>
        Assert.Equal(RecordingHotkeyDecision.Ignore, RecordingHotkeyPolicy.Decide(true, false, sameMode: false, gateOpen: true));

    [Fact]
    public void Processing_CancelsWhenGateOpen_RegardlessOfMode()
    {
        Assert.Equal(RecordingHotkeyDecision.Cancel, RecordingHotkeyPolicy.Decide(false, true, sameMode: false, gateOpen: true));
        Assert.Equal(RecordingHotkeyDecision.Cancel, RecordingHotkeyPolicy.Decide(false, true, sameMode: true, gateOpen: true));
        Assert.Equal(RecordingHotkeyDecision.Ignore, RecordingHotkeyPolicy.Decide(false, true, sameMode: true, gateOpen: false));
    }
}

public class CaptureWatchdogTests
{
    [Fact]
    public void Stall_IsThreeSecondsWithoutABuffer()
    {
        Assert.False(CaptureWatchdog.IsStalled(0, 2999));
        Assert.True(CaptureWatchdog.IsStalled(0, 3000));
        Assert.True(CaptureWatchdog.IsStalled(1000, 4000));
        Assert.False(CaptureWatchdog.IsStalled(1000, 3999));
    }

    [Fact]
    public void CustomThreshold_IsHonoured()
    {
        Assert.False(CaptureWatchdog.IsStalled(0, 500, stallMs: 1000));
        Assert.True(CaptureWatchdog.IsStalled(0, 1000, stallMs: 1000));
    }
}
