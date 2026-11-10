using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class RecordingKindTests
{
    [Theory]
    [InlineData("Dictate", RecordingKindName.Dictate)]
    [InlineData("Prompt", RecordingKindName.Prompt)]
    [InlineData("prompt", RecordingKindName.Prompt)]
    [InlineData("Live", RecordingKindName.Live)]
    [InlineData("Template", RecordingKindName.Template)] // written by older builds, no id
    public void Parse_PlainValues(string text, RecordingKindName kind) =>
        Assert.Equal((kind, (string?)null), RecordingKind.Parse(text));

    [Fact]
    public void Parse_TemplateWithId()
    {
        var id = "3f2b8c1e-0000-4000-8000-000000000001";
        Assert.Equal((RecordingKindName.Template, (string?)id), RecordingKind.Parse("Template:" + id));
        Assert.Equal((RecordingKindName.Template, (string?)null), RecordingKind.Parse("Template:"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Banana")]
    public void Parse_UnknownIsDictate(string? text) =>
        Assert.Equal((RecordingKindName.Dictate, (string?)null), RecordingKind.Parse(text));

    [Fact]
    public void Format_RoundTrips()
    {
        Assert.Equal("Dictate", RecordingKind.Format(RecordingKindName.Dictate));
        Assert.Equal("Prompt", RecordingKind.Format(RecordingKindName.Prompt));
        Assert.Equal("Live", RecordingKind.Format(RecordingKindName.Live));
        Assert.Equal("Template", RecordingKind.Format(RecordingKindName.Template));
        Assert.Equal("Template:abc", RecordingKind.Format(RecordingKindName.Template, "abc"));
        Assert.Equal((RecordingKindName.Template, (string?)"abc"), RecordingKind.Parse(RecordingKind.Format(RecordingKindName.Template, "abc")));
        Assert.Equal((RecordingKindName.Live, (string?)null), RecordingKind.Parse(RecordingKind.Format(RecordingKindName.Live)));
    }
}
