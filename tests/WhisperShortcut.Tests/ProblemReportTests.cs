using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ProblemReportTests
{
    private static readonly ProblemReportInfo Info = new("1.2.3", "Windows 11 10.0.26100", "Arm64", true,
        "gemini-3.1-flash-lite", "gemini-3.8-flash", "qwen3-4b");

    [Fact]
    public void Body_HasSectionsInOrder()
    {
        var body = ProblemReport.Body(Info, new[] { "line one", "line two" });
        int what = body.IndexOf("**Was ist passiert?**", StringComparison.Ordinal);
        int env = body.IndexOf("**Umgebung**", StringComparison.Ordinal);
        int log = body.IndexOf("**Protokoll (letzte Zeilen)**", StringComparison.Ordinal);
        Assert.True(0 <= what && what < env && env < log);
        // Blank space to type into between the heading and the next section.
        Assert.Contains("**Was ist passiert?**\n\n\n\n**Umgebung**", body);
        Assert.Contains("- Version: 1.2.3", body);
        Assert.Contains("- Modus: offline", body);
        Assert.DoesNotContain("Profil", body);
        Assert.Contains("```\nline one\nline two\n```", body);
    }

    [Fact]
    public void Body_RedactsKeysInLogLines()
    {
        var body = ProblemReport.Body(Info, new[] { "key AIzaSyA1234567890abcdefghijklmnop leaked" });
        Assert.DoesNotContain("AIzaSyA1234567890", body);
        Assert.Contains("key AIza… leaked", body);
    }

    [Fact]
    public void Redact_LeavesShortAndPlainText()
    {
        Assert.Equal("AIza short", ProblemReport.Redact("AIza short"));
        Assert.Equal("nothing here", ProblemReport.Redact("nothing here"));
    }

    [Fact]
    public void IssueUrl_EscapesTitleAndBody()
    {
        var (url, inUrl) = ProblemReport.IssueUrl("A & B", "x y\nz");
        Assert.True(inUrl);
        Assert.StartsWith("https://github.com/mgsgde/whisper-shortcut-windows/issues/new?title=A%20%26%20B&body=", url);
        Assert.EndsWith("x%20y%0Az", url);
    }

    [Fact]
    public void IssueUrl_LongBodyFallsBackToClipboardHint()
    {
        var (url, inUrl) = ProblemReport.IssueUrl("t", new string('ä', 5000));
        Assert.False(inUrl);
        Assert.True(url.Length <= ProblemReport.MaxUrlLength);
        Assert.Contains(Uri.EscapeDataString(ProblemReport.ClipboardHint), url);
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
