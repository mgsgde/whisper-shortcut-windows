using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class DiskSpaceTests
{
    [Theory]
    [InlineData(DiskSpace.DiskFullHResult, true)]
    [InlineData(DiskSpace.EnospcHResult, true)]
    [InlineData(28, true)]
    [InlineData(unchecked((int)0x80070005), false)] // access denied
    [InlineData(0, false)]
    public void IsFull_MatchesDiskFullHResults(int hresult, bool expected)
    {
        var ex = new IOException("write failed") { HResult = hresult };
        Assert.Equal(expected, DiskSpace.IsFull(ex));
        Assert.Equal(expected, DiskSpace.IsFull((Exception)ex));
    }

    [Fact]
    public void IsFull_NonIoException_IsFalse() =>
        Assert.False(DiskSpace.IsFull(new InvalidOperationException("no")));

    [Fact]
    public void DiskFullException_KeepsGermanMessage_AndDiskFullHResult()
    {
        var inner = new IOException("ENOSPC") { HResult = 28 };
        var ex = new DiskFullException(DiskSpace.SettingsMessage, inner);
        Assert.Equal(DiskSpace.SettingsMessage, ex.Message);
        Assert.Equal(DiskSpace.DiskFullHResult, ex.HResult);
        Assert.Same(inner, ex.InnerException);
        Assert.True(DiskSpace.IsFull(ex));
    }
}
