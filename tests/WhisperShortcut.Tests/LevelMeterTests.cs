using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class LevelMeterTests
{
    [Fact]
    public void RisingLevelIsTakenAtOnceAsItsSquareRoot()
    {
        var meter = new LevelMeter(0.8f);
        Assert.Equal(0.5f, meter.Feed(0.25f));
        Assert.Equal(0.5f, meter.Smoothed);
        Assert.Equal(0.25f, meter.Peak);
    }

    [Fact]
    public void FallingLevelReleasesSlowly()
    {
        var meter = new LevelMeter(0.8f);
        meter.Feed(1f);
        Assert.Equal(0.8f, meter.Feed(0f), 3);
    }

    [Fact]
    public void LevelIsClampedToZeroAndOne()
    {
        var meter = new LevelMeter(0.8f);
        Assert.Equal(1f, meter.Feed(2f));
        Assert.Equal(1f, meter.Peak);
        var quiet = new LevelMeter(0.8f);
        Assert.Equal(0f, quiet.Feed(-1f));
        Assert.Equal(0f, quiet.Peak);
    }

    [Fact]
    public void ResetForgetsBarsAndPeak()
    {
        var meter = new LevelMeter(0.8f);
        meter.Feed(1f);
        meter.Reset();
        Assert.Equal(0f, meter.Smoothed);
        Assert.Equal(0f, meter.Peak);
    }

    [Fact]
    public void SilentNeedsLowPeakAndMoreThanTheWaitingTime()
    {
        var after = TimeSpan.FromSeconds(3);
        Assert.True(LevelMeter.IsSilent(0.005f, TimeSpan.FromSeconds(3.1), after));
        Assert.False(LevelMeter.IsSilent(0.005f, TimeSpan.FromSeconds(3), after));
        Assert.False(LevelMeter.IsSilent(0.01f, TimeSpan.FromSeconds(10), after));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3723, "1:02:03")]
    public void ClockShowsMinutesAndHoursOnlyWhenNeeded(int seconds, string expected) =>
        Assert.Equal(expected, Clock.Format(TimeSpan.FromSeconds(seconds)));
}
