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
        // Mac #468 inserted the additions helpers above these constants, so they no longer
        // fit in lines 7–90. The names are unchanged; the fixed-core architecture is not ported.
        var t = Swift("AppConstants.swift", 7, 120);
        Assert.Equal(Prompts.DefaultTranscriptionSystemPrompt, Multi(t, "defaultTranscriptionSystemPrompt"));
        Assert.Equal(Prompts.DefaultPromptModeSystemPrompt, Multi(t, "defaultPromptModeSystemPrompt"));
        Assert.Equal(Prompts.PromptModeOutputRule, Single(t, "promptModeOutputRule"));
        Assert.Equal(Prompts.DictatePromptComposeMarker, Single(t, "dictatePromptComposeMarker"));
        Assert.Equal(Prompts.ClipboardSelectionHeader, Single(t, "clipboardSelectionHeader"));
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
        Assert.Equal(Prompts.GlossaryHintHeader, string.Concat(literals));
    }
}
