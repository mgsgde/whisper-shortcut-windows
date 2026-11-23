using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ProblemReportTests
{
    [Fact]
    public void LogSection_WrapsRedactedLines()
    {
        var section = ProblemReport.LogSection(new[] { "line one", "key AIzaSyA1234567890abcdefghijklmnop leaked" });
        Assert.Equal("## Log (last lines)\n```\nline one\nkey AIza… leaked\n```", section);
        Assert.DoesNotContain("AIzaSyA1234567890", section);
    }

    [Fact]
    public void LogSection_IsEmptyWithoutLines() => Assert.Equal("", ProblemReport.LogSection(Array.Empty<string>()));

    [Fact]
    public void Redact_LeavesShortAndPlainText()
    {
        Assert.Equal("AIza short", ProblemReport.Redact("AIza short"));
        Assert.Equal("sk-short", ProblemReport.Redact("sk-short"));
        Assert.Equal("nothing here", ProblemReport.Redact("nothing here"));
    }

    [Fact]
    public void TailLog_ReturnsLastLines_OrAllIfFewer()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(path, Enumerable.Range(1, 10).Select(i => $"l{i}"));
        Assert.Equal(new[] { "l8", "l9", "l10" }, ProblemReport.TailLog(path, 3));
        Assert.Equal(10, ProblemReport.TailLog(path, 50).Count);
        File.Delete(path);
    }

    [Fact]
    public void TailLog_ReadsFileThatIsOpenForWriting()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".log");
        using (var w = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)))
        {
            w.WriteLine("busy AIzaSyA1234567890abcdefghijklmnop");
            w.Flush();
            Assert.Equal(new[] { "busy AIza…" }, ProblemReport.TailLog(path));
        }
        File.Delete(path);
    }

    [Fact]
    public void TailLog_MissingFile_IsEmpty()
    {
        Assert.Empty(ProblemReport.TailLog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope.log")));
    }
}
