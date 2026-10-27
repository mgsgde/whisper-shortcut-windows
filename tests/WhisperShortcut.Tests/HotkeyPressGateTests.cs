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
