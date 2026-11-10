using System.Text;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class StreamingTests
{
    private static async Task<List<string>> Payloads(string sse, Action? onLine = null)
    {
        var list = new List<string>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        await foreach (var p in Sse.DataPayloads(stream, default, onLine)) list.Add(p);
        return list;
    }

    [Fact]
    public async Task DataPayloads_AreTrimmed_EmptyOnesAndOtherLinesSkipped()
    {
        var got = await Payloads(": comment\r\n\r\nevent: x\r\ndata: {\"a\":1}\r\n\r\ndata:{\"b\":2}\r\ndata:   \r\ndata:\r\nid: 3\r\ndata: tail");
        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}", "tail" }, got);
    }

    [Fact]
    public async Task DataPayloads_PassTheDoneMarkerOn()
    {
        Assert.Equal(new[] { "{}", "[DONE]" }, await Payloads("data: {}\n\ndata: [DONE]\n\n"));
    }

    [Fact]
    public async Task DataPayloads_ReportEveryLineToOnLine()
    {
        var lines = 0;
        await Payloads(": ping\ndata: a\n\ndata: b\n", () => lines++);
        Assert.Equal(4, lines);   // comment, data, blank, data; the final newline ends the stream
    }

    [Fact]
    public async Task DataPayloads_ReadUtf8()
    {
        Assert.Equal(new[] { "Grüße – äöü" }, await Payloads("data: Grüße – äöü\n"));
    }

    [Fact]
    public async Task Watchdog_FiresWhenIdle_RearmPostponesIt_CallerLinkCancels()
    {
        using var idle = new IdleWatchdog(default, TimeSpan.FromMilliseconds(400));
        idle.Rearm(TimeSpan.FromSeconds(30));
        await Task.Delay(450);
        Assert.False(idle.Token.IsCancellationRequested);
        idle.Rearm(TimeSpan.FromMilliseconds(20));
        await Task.Delay(300);
        Assert.True(idle.Token.IsCancellationRequested);

        using var caller = new CancellationTokenSource();
        using var linked = new IdleWatchdog(caller.Token, TimeSpan.FromSeconds(30));
        caller.Cancel();
        Assert.True(linked.Token.IsCancellationRequested);
    }
}
