using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>The capture-side queue of a live dictation (TrayApp's LiveRun): cuts, order, tail, close.</summary>
public class LivePieceFeedTests
{
    private const int Rate = 16000;

    private static byte[] Tone(double seconds, double db)
    {
        var amplitude = Math.Sqrt(2) * 32768 * Math.Pow(10, db / 20);
        var s = new short[(int)Math.Round(seconds * Rate)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * 440 * i / Rate));
        var bytes = new byte[s.Length * 2];
        Buffer.BlockCopy(s, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static bool Feed(LivePieceFeed feed, byte[] audio)
    {
        bool any = false;
        for (var i = 0; i < audio.Length; i += 3200) // 100 ms buffers, like the microphone
            any |= feed.Write(audio.AsSpan(i, Math.Min(3200, audio.Length - i)));
        return any;
    }

    [Fact]
    public void A_pause_cuts_a_piece_that_is_queued_in_order_with_the_clock_of_its_cut()
    {
        long now = 100;
        var feed = new LivePieceFeed(clock: () => now++);
        var audio = Tone(2, -20).Concat(Tone(1.5, -90)).Concat(Tone(2, -20)).Concat(Tone(1.5, -90)).ToArray();
        Assert.True(Feed(feed, audio));
        Assert.True(feed.TryTake(out var first));
        Assert.True(feed.TryTake(out var second));
        Assert.Equal(0, first.Chunk.Index);
        Assert.Equal(1, second.Chunk.Index);
        Assert.True(second.Cut > first.Cut);
        Assert.False(feed.TryTake(out _));
    }

    [Fact]
    public void Finish_returns_the_rest_once_and_closes_the_feed()
    {
        var feed = new LivePieceFeed();
        Assert.False(Feed(feed, Tone(2, -20))); // no pause yet: nothing cut
        Assert.False(feed.HasPieces);
        var tail = feed.Finish();
        Assert.NotNull(tail);
        Assert.InRange(tail!.DurationSeconds, 1.9, 2.1);
        Assert.Null(feed.Finish());
        Assert.False(Feed(feed, Tone(40, -20))); // a late buffer after the end is dropped
        Assert.False(feed.HasPieces);
    }

    [Fact]
    public void Close_drops_the_waiting_pieces_and_later_audio()
    {
        var feed = new LivePieceFeed();
        Assert.True(Feed(feed, Tone(31, -20))); // the 30 s maximum cuts without a pause
        Assert.True(feed.HasPieces);
        feed.Close();
        Assert.False(feed.HasPieces);
        Assert.False(feed.TryTake(out _));
        Assert.False(Feed(feed, Tone(31, -20)));
        Assert.Null(feed.Finish());
    }

    [Fact]
    public void An_odd_trailing_byte_is_ignored()
    {
        var feed = new LivePieceFeed();
        feed.Write(new byte[3201]);
        var tail = feed.Finish();
        Assert.True(tail == null || tail.Wav.Length > 0);
    }

    [Fact]
    public async Task Pieces_written_on_the_capture_thread_are_taken_on_another_in_order()
    {
        var feed = new LivePieceFeed();
        var audio = Tone(65, -20);
        var taken = 0;
        await Task.Run(() => Feed(feed, audio));
        while (feed.TryTake(out _)) taken++;
        Assert.Equal(2, taken); // 30 s, 30 s; 5 s remain for Finish
        Assert.NotNull(feed.Finish());
    }
}
