using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// Port of <c>DictatePromptSelectionDecision.isBareEditCommand</c> (DictatePromptSelection.swift:51-104):
/// "korrigiere" with nothing selected must be refused, otherwise the model invents the text it "edits".
/// Extra words ("korrigiere, mein Geburtsdatum ist …", "schreib eine Notiz") are a real compose request.
/// </summary>
public static class BareEditCommand
{
    private static readonly Regex Pattern = new(
        "^(?:korrigiere|korrigier das|korrigieren|formuliere(?: das)? (?:neu|um)|schreib(?: das)? um|umschreiben|kürzer|kürze(?: das)?|mach (?:es|das) kürzer|verbessere(?: das)?|correct(?: this| that)?|fix (?:this|that|it)|rephrase(?: this| that)?|shorten(?: this| that)?|make (?:it|this) shorter|proofread(?: this)?)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the transcribed instruction is only an edit command and nothing else.
    /// Windows addition: "mach das freundlicher" and similar are bare edits too.
    /// </summary>
    public static bool Matches(string? instruction)
    {
        var text = (instruction ?? "").Trim().ToLowerInvariant();
        text = text.TrimEnd('.', '!', '?');
        text = StripLeadingFiller(text.Trim());
        if (text.Length == 0) return false;
        return Pattern.IsMatch(text) || FriendlierPattern.IsMatch(text);
    }

    private static readonly Regex FriendlierPattern = new(
        "^mach (?:es|das|den text)(?: etwas| ein bisschen| bitte)? (?:freundlicher|höflicher|netter|förmlicher|kürzer|länger)(?: bitte)?$",
        RegexOptions.CultureInvariant);

    private static string StripLeadingFiller(string text)
    {
        foreach (var filler in new[] { "ähm", "äh", "hm" })
        {
            if (!text.StartsWith(filler, StringComparison.Ordinal)) continue;
            var rest = text[filler.Length..];
            if (rest.StartsWith(',')) rest = rest[1..];
            else if (rest.Length > 0 && !char.IsWhiteSpace(rest[0])) continue;
            return rest.Trim();
        }
        return text;
    }
}
