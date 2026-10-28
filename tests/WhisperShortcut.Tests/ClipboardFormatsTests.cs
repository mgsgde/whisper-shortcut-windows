using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ClipboardFormatsTests
{
    [Fact]
    public void DibIsDroppedWhenDibV5IsPresent()
    {
        Assert.Equal(new uint[] { 17 }, ClipboardFormats.ToSnapshot(new uint[] { 8, 17 }));
    }

    [Fact]
    public void DibAloneIsKept()
    {
        Assert.Equal(new uint[] { 8 }, ClipboardFormats.ToSnapshot(new uint[] { 8 }));
    }

    [Fact]
    public void AnsiAndOemTextAreDroppedWhenUnicodeTextIsPresent()
    {
        Assert.Equal(new uint[] { 13, 16 }, ClipboardFormats.ToSnapshot(new uint[] { 13, 1, 7, 16 }));
    }

    [Fact]
    public void TextAloneIsKept()
    {
        Assert.Equal(new uint[] { 1 }, ClipboardFormats.ToSnapshot(new uint[] { 1 }));
    }

    [Fact]
    public void BitmapAndPrivateRangeAreDropped()
    {
        Assert.Equal(new uint[] { 13 }, ClipboardFormats.ToSnapshot(new uint[] { 2, 13, 0x250 }));
    }

    [Fact]
    public void OrderIsPreserved()
    {
        Assert.Equal(new uint[] { 49161, 17, 13, 16 },
            ClipboardFormats.ToSnapshot(new uint[] { 49161, 8, 17, 1, 13, 16, 7 }));
    }

    [Fact]
    public void DuplicatesAreDropped()
    {
        Assert.Equal(new uint[] { 17, 13, 16 },
            ClipboardFormats.ToSnapshot(new uint[] { 17, 13, 17, 16, 13, 8 }));
    }

    [Fact]
    public void IsMemoryFormatRejectsHandlesThatAreNotMemory()
    {
        Assert.False(ClipboardFormats.IsMemoryFormat(2));
        Assert.False(ClipboardFormats.IsMemoryFormat(3));
        Assert.False(ClipboardFormats.IsMemoryFormat(9));
        Assert.False(ClipboardFormats.IsMemoryFormat(14));
        Assert.False(ClipboardFormats.IsMemoryFormat(0x80));
        Assert.False(ClipboardFormats.IsMemoryFormat(0x82));
        Assert.False(ClipboardFormats.IsMemoryFormat(0x83));
        Assert.False(ClipboardFormats.IsMemoryFormat(0x8E));
        Assert.False(ClipboardFormats.IsMemoryFormat(0x200));
        Assert.False(ClipboardFormats.IsMemoryFormat(0x250));
        Assert.False(ClipboardFormats.IsMemoryFormat(0x3FF));

        Assert.True(ClipboardFormats.IsMemoryFormat(1));
        Assert.True(ClipboardFormats.IsMemoryFormat(7));
        Assert.True(ClipboardFormats.IsMemoryFormat(8));
        Assert.True(ClipboardFormats.IsMemoryFormat(13));
        Assert.True(ClipboardFormats.IsMemoryFormat(16));
        Assert.True(ClipboardFormats.IsMemoryFormat(17));
        Assert.True(ClipboardFormats.IsMemoryFormat(0x1FF));
        Assert.True(ClipboardFormats.IsMemoryFormat(0x400));
        Assert.True(ClipboardFormats.IsMemoryFormat(49161));
    }
}
