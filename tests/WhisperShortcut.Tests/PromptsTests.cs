using System.Text.RegularExpressions;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class PromptsTests
{
    private static string Swift(string file, int from, int to) =>
        string.Join("\n", File.ReadAllLines(Paths.SwiftFile(file)).Skip(from - 1).Take(to - from + 1));

    private static string Multi(string t, string name) =>
        Regex.Match(t, "static let " + name + @" =\n\s*""""""\n(.*?)\n""""""", RegexOptions.Singleline).Groups[1].Value;

    // Unescapes a single-line Swift string literal (only \" and \n occur).
    private static string Single(string t, string name)
    {
        var m = Regex.Match(t, "static let " + name + @" =\n\s*""((?:[^""\\\n]|\\.)*)""\n");
        Assert.True(m.Success, name);
        return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\n", "\n");
    }

    [SwiftSourceFact]
    public void PromptsEqualSwiftSource()
    {
        var t = Swift("AppConstants.swift", 7, 90);
        Assert.Equal(Multi(t, "defaultTranscriptionSystemPrompt"), Prompts.DefaultTranscriptionSystemPrompt);
        Assert.Equal(Multi(t, "defaultPromptModeSystemPrompt"), Prompts.DefaultPromptModeSystemPrompt);
        Assert.Equal(Single(t, "promptModeOutputRule"), Prompts.PromptModeOutputRule);
        Assert.Equal(Single(t, "dictatePromptComposeMarker"), Prompts.DictatePromptComposeMarker);
        Assert.Equal(Single(t, "clipboardSelectionHeader"), Prompts.ClipboardSelectionHeader);
        Assert.StartsWith("You are a professional transcription service.", Prompts.DefaultTranscriptionSystemPrompt);
        Assert.EndsWith("Start directly with the transcribed words.", Prompts.DefaultTranscriptionSystemPrompt);
    }

    [SwiftSourceFact]
    public void GlossaryHintEqualsSwiftSource()
    {
        var t = File.ReadAllText(Paths.SwiftFile("SpeechService.swift")).ReplaceLineEndings("\n");
        var start = t.IndexOf("block = \"Reference vocabulary", StringComparison.Ordinal);
        var end = t.IndexOf("+ glossary", start, StringComparison.Ordinal);
        var literals = Regex.Matches(t[start..end], "\"((?:[^\"\\\\\\n]|\\\\.)*)\"")
            .Select(m => m.Groups[1].Value.Replace("\\n", "\n"));
        Assert.Equal(string.Concat(literals), Prompts.GlossaryHintHeader);
    }
}
