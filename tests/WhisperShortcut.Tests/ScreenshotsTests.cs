using System.Buffers.Binary;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ScreenshotsTests
{
    private static readonly byte[] Sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static byte[] Png(int length)
    {
        var d = new byte[length];
        Sig.CopyTo(d, 0);
        return d;
    }

    // ---- ScreenshotImage.FromPng ----

    [Fact]
    public void FromPng_ValidSignature_ReturnsImage()
    {
        var data = Png(20);
        var img = ScreenshotImage.FromPng(data);
        Assert.NotNull(img);
        Assert.Equal("image/png", img!.MimeType);
        Assert.Same(data, img.Data);
    }

    [Fact]
    public void FromPng_WrongSignature_Null()
    {
        var data = Png(20);
        data[1] = 0;
        Assert.Null(ScreenshotImage.FromPng(data));
    }

    [Fact]
    public void FromPng_EmptyNullOrShort_Null()
    {
        Assert.Null(ScreenshotImage.FromPng(null));
        Assert.Null(ScreenshotImage.FromPng(Array.Empty<byte>()));
        Assert.Null(ScreenshotImage.FromPng(Sig[..5]));
    }

    [Fact]
    public void FromPng_LimitIsInclusive()
    {
        Assert.NotNull(ScreenshotImage.FromPng(Png((int)ScreenshotImage.MaxBytes)));
        Assert.Null(ScreenshotImage.FromPng(Png((int)ScreenshotImage.MaxBytes + 1)));
    }

    // ---- DibImage.ToBmp ----

    private static byte[] Dib(uint biSize, ushort bitCount, uint compression, uint clrUsed, int total)
    {
        var d = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(d, biSize);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(14), bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(32), clrUsed);
        for (var i = (int)biSize; i < total; i++) d[i] = (byte)(i % 251);
        return d;
    }

    private static uint OffBits(byte[] bmp) => BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(10));

    [Fact]
    public void ToBmp_24Bit_OffsetAndSize()
    {
        var dib = Dib(40, 24, 0, 0, 40 + 100);
        var bmp = DibImage.ToBmp(dib)!;
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(54u, OffBits(bmp));
        Assert.Equal((uint)bmp.Length, BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(2)));
        Assert.Equal(dib.Length + 14, bmp.Length);
        Assert.Equal(dib, bmp[14..]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(6)));
    }

    [Fact]
    public void ToBmp_8Bit_NoClrUsed_Has256Colors()
    {
        var bmp = DibImage.ToBmp(Dib(40, 8, 0, 0, 40 + 1024 + 10))!;
        Assert.Equal(14u + 40 + 256 * 4, OffBits(bmp));
    }

    [Fact]
    public void ToBmp_8Bit_ClrUsedWins()
    {
        var bmp = DibImage.ToBmp(Dib(40, 8, 0, 16, 40 + 64 + 10))!;
        Assert.Equal(14u + 40 + 16 * 4, OffBits(bmp));
    }

    [Fact]
    public void ToBmp_Bitfields32Bit_AddsMasks()
    {
        var bmp = DibImage.ToBmp(Dib(40, 32, 3, 0, 40 + 12 + 16))!;
        Assert.Equal(14u + 40 + 12, OffBits(bmp));
    }

    [Fact]
    public void ToBmp_V5Header_NoExtraMasks()
    {
        var bmp = DibImage.ToBmp(Dib(124, 32, 3, 0, 124 + 16))!;
        Assert.Equal(14u + 124, OffBits(bmp));
    }

    [Fact]
    public void ToBmp_TooShortOrSmallHeader_Null()
    {
        Assert.Null(DibImage.ToBmp(null));
        Assert.Null(DibImage.ToBmp(new byte[39]));
        Assert.Null(DibImage.ToBmp(Dib(12, 24, 0, 0, 60)));
    }

    [Fact]
    public void ToBmp_HeaderClaimsMoreThanData_Null()
    {
        // 8-bit needs a 1024-byte color table, but only 100 bytes follow the header.
        Assert.Null(DibImage.ToBmp(Dib(40, 8, 0, 0, 140)));
    }

    // ---- ScreenshotHold ----

    private sealed class Clock
    {
        public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
    }

    private static ScreenshotImage Img(byte b = 1) => new(new[] { b }, "image/png");

    [Fact]
    public void Hold_TakeOnce()
    {
        var hold = new ScreenshotHold(new Clock().Get);
        Assert.False(hold.HasImage);
        Assert.Null(hold.Take());
        var img = Img();
        hold.Hold(img);
        Assert.True(hold.HasImage);
        Assert.Same(img, hold.Take());
        Assert.False(hold.HasImage);
        Assert.Null(hold.Take());
    }

    [Fact]
    public void Hold_ReplacesEarlierImage_AndRestartsLifetime()
    {
        var clock = new Clock();
        var hold = new ScreenshotHold(clock.Get);
        hold.Hold(Img(1));
        clock.Now += TimeSpan.FromMinutes(4);
        var second = Img(2);
        hold.Hold(second);
        clock.Now += TimeSpan.FromMinutes(4);
        Assert.Same(second, hold.Take());
    }

    [Fact]
    public void Expiry_AtExactlyLifetime_Drops_JustBeforeKeeps()
    {
        var clock = new Clock();
        var hold = new ScreenshotHold(clock.Get);
        hold.Hold(Img());
        clock.Now += ScreenshotHold.Lifetime - TimeSpan.FromTicks(1);
        Assert.True(hold.HasImage);
        clock.Now += TimeSpan.FromTicks(1);
        Assert.False(hold.HasImage);
        Assert.Null(hold.Take());
    }

    [Fact]
    public void Take_AfterExpiry_Null()
    {
        var clock = new Clock();
        var hold = new ScreenshotHold(clock.Get);
        hold.Hold(Img());
        clock.Now += ScreenshotHold.Lifetime;
        Assert.Null(hold.Take());
    }

    [Fact]
    public void DropIfExpired_TrueOnlyWhenSomethingDropped()
    {
        var clock = new Clock();
        var hold = new ScreenshotHold(clock.Get);
        Assert.False(hold.DropIfExpired());
        hold.Hold(Img());
        Assert.False(hold.DropIfExpired());
        clock.Now += ScreenshotHold.Lifetime;
        Assert.True(hold.DropIfExpired());
        Assert.False(hold.DropIfExpired());
    }

    [Fact]
    public void Clear_DropsImage()
    {
        var hold = new ScreenshotHold(new Clock().Get);
        hold.Hold(Img());
        hold.Clear();
        Assert.False(hold.HasImage);
        Assert.Null(hold.Take());
    }

    // ---- ScreenshotPolicy ----

    private static Settings Make(Action<Settings> set)
    {
        var s = Settings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"), new PlainSecretProtector());
        set(s);
        return s;
    }

    [Fact]
    public void Policy_OffByDefault()
    {
        Assert.False(ScreenshotPolicy.Allowed(Make(_ => { })));
    }

    [Fact]
    public void Policy_OnAndOnline_Allowed()
    {
        Assert.True(ScreenshotPolicy.Allowed(Make(s => { s.SendScreenshots = true; s.TextEngine = Engine.Gemini; })));
        Assert.True(ScreenshotPolicy.Allowed(Make(s => { s.SendScreenshots = true; s.TextEngine = Engine.OpenAI; })));
    }

    [Fact]
    public void Policy_OfflineModeOrLocalEngine_Denied()
    {
        Assert.False(ScreenshotPolicy.Allowed(Make(s => { s.SendScreenshots = true; s.OfflineMode = true; })));
        Assert.False(ScreenshotPolicy.Allowed(Make(s => { s.SendScreenshots = true; s.TextEngine = Engine.Local; })));
    }

    // ---- SnipWatch ----

    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Snip_ChangedWithImage_Captured()
    {
        Assert.Equal(SnipStep.Captured, new SnipWatch().Next(S(3), true, true, true));
    }

    [Fact]
    public void Snip_CapturedBeatsTimeout()
    {
        Assert.Equal(SnipStep.Captured, new SnipWatch().Next(SnipWatch.Timeout, true, true, false));
    }

    [Fact]
    public void Snip_TimesOut()
    {
        var w = new SnipWatch();
        Assert.Equal(SnipStep.Waiting, w.Next(S(59.9), false, false, true));
        Assert.Equal(SnipStep.TimedOut, w.Next(SnipWatch.Timeout, false, false, true));
    }

    [Fact]
    public void Snip_ChangedWithoutImage_CancelsAfterGrace()
    {
        var w = new SnipWatch();
        Assert.Equal(SnipStep.Waiting, w.Next(S(1), true, false, true));
        Assert.Equal(SnipStep.Waiting, w.Next(S(2.9), true, false, true));
        Assert.Equal(SnipStep.Cancelled, w.Next(S(3), true, false, true));
    }

    [Fact]
    public void Snip_ChangedWithoutImage_ThenImageWithinGrace_Captured()
    {
        var w = new SnipWatch();
        Assert.Equal(SnipStep.Waiting, w.Next(S(1), true, false, true));
        Assert.Equal(SnipStep.Captured, w.Next(S(2), true, true, true));
    }

    [Fact]
    public void Snip_UiSeenThenGone_CancelsAfterGrace()
    {
        var w = new SnipWatch();
        Assert.Equal(SnipStep.Waiting, w.Next(S(1), false, false, true));
        Assert.Equal(SnipStep.Waiting, w.Next(S(2), false, false, false));
        Assert.Equal(SnipStep.Waiting, w.Next(S(3.4), false, false, false));
        Assert.Equal(SnipStep.Cancelled, w.Next(S(3.5), false, false, false));
    }

    [Fact]
    public void Snip_UiReappearing_ResetsClosedTimer()
    {
        var w = new SnipWatch();
        Assert.Equal(SnipStep.Waiting, w.Next(S(1), false, false, true));
        Assert.Equal(SnipStep.Waiting, w.Next(S(2), false, false, false));
        Assert.Equal(SnipStep.Waiting, w.Next(S(3), false, false, true));
        Assert.Equal(SnipStep.Waiting, w.Next(S(4), false, false, false));
        Assert.Equal(SnipStep.Waiting, w.Next(S(5.4), false, false, false));
        Assert.Equal(SnipStep.Cancelled, w.Next(S(5.5), false, false, false));
    }

    [Fact]
    public void Snip_UiNeverSeen_WaitsUntilTimeout()
    {
        var w = new SnipWatch();
        for (var t = 0.0; t < 60; t += 5)
            Assert.Equal(SnipStep.Waiting, w.Next(S(t), false, false, false));
        Assert.Equal(SnipStep.TimedOut, w.Next(S(60), false, false, false));
    }
}
