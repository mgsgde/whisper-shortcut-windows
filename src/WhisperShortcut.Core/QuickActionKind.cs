namespace WhisperShortcut.Core;

/// <summary>
/// Coarse intent of a quick instruction; drives the symbol in front of its row in the Dictate Prompt card.
/// Port of <c>QuickActionKind</c> (QuickActionStore.swift:15-70).
/// </summary>
public enum QuickActionKind { Correct, Rewrite, Shorten, Format, Translate, Other }

public static class QuickActionKinds
{
    // A token must start a word, so "information" is not "format"; tokens are stems ("kürz" → "kürzer").
    private static readonly string[] NotVerbs = { "shortcut", "shortage", "tablet", "fixture", "bulletin", "formation" };
    private static readonly string[] ShortenTokens = { "kürz", "kurz", "short", "concise", "condense", "raccourc", "acorta" };
    private static readonly string[] TranslateTokens = { "übersetz", "translat", "tradui", "tradu" };
    private static readonly string[] FormatTokens = { "format", "to-do", "todo", "bullet", "aufzähl", "markdown", "tabelle", "table" };
    private static readonly string[] CorrectTokens =
        { "korrig", "corrig", "correct", "fix", "grammat", "grammar", "rechtschreib", "spelling", "tippfehler", "typo", "proofread" };
    private static readonly string[] RewriteTokens =
        { "formulier", "rewrite", "rephrase", "reword", "umschreib", "reformul", "klarer", "clearer" };

    /// <summary>First match wins, in the Mac's order: shorten, translate, format, correct, rewrite.</summary>
    public static QuickActionKind Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return QuickActionKind.Other;
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-') current.Append(c);
            else if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) words.Add(current.ToString());

        bool Has(string[] tokens) => words.Any(w =>
            !NotVerbs.Any(n => w.StartsWith(n, StringComparison.Ordinal)) && tokens.Any(t => w.StartsWith(t, StringComparison.Ordinal)));

        if (Has(ShortenTokens)) return QuickActionKind.Shorten;
        if (Has(TranslateTokens)) return QuickActionKind.Translate;
        if (Has(FormatTokens)) return QuickActionKind.Format;
        if (Has(CorrectTokens)) return QuickActionKind.Correct;
        if (Has(RewriteTokens)) return QuickActionKind.Rewrite;
        return QuickActionKind.Other;
    }

    /// <summary>
    /// Needs a selection to act on (Mac <c>QuickAction.isEdit</c>): a built-in default, or an instruction that is only an
    /// edit verb. The card shows such rows dimmed while nothing is selected.
    /// </summary>
    public static bool IsEdit(string text) =>
        QuickActionList.Defaults.Contains(text) || BareEditCommand.Matches(text);
}
