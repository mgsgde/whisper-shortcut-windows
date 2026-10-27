namespace WhisperShortcut.Core;

/// <summary>The quick instructions offered while recording, and their cleanup.</summary>
public static class QuickActionList
{
    /// <summary>Most quick actions kept (one per number key).</summary>
    public const int Max = 5;

    /// <summary>Longest accepted quick action in characters; longer lines are dropped.</summary>
    public const int MaxLength = 80;

    /// <summary>The five actions a new or old settings file starts with (neutral, same as the macOS app).</summary>
    public static IReadOnlyList<string> Defaults { get; } = new[]
    {
        "Rechtschreibung und Grammatik korrigieren",
        "Klarer formulieren",
        "Kürzer fassen",
        "Als Aufgabenliste formatieren",
        "Ins Englische übersetzen",
    };

    // WHY: the first versions shipped a medical default list; a file that still holds exactly it was never customised.
    private static readonly string[] LegacyDefaults =
    {
        "Als Anamnese, Befund und Therapie gliedern",
        "In Stichpunkten zusammenfassen",
        "Rechtschreibung und Grammatik korrigieren",
        "Kürzer fassen",
        "Freundlicher formulieren",
    };

    /// <summary>True when <paramref name="saved"/> is exactly the old medical default list (same strings, same order).</summary>
    public static bool IsLegacyDefaults(IReadOnlyList<string> saved) => saved.SequenceEqual(LegacyDefaults);

    /// <summary>
    /// Trims, collapses inner whitespace, drops blank and too long lines, dedupes case-insensitively
    /// (first wins) and keeps at most <see cref="Max"/>. WHY: the list comes from a free-text box.
    /// </summary>
    public static List<string> Clean(IEnumerable<string?> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (line == null) continue;
            var cleaned = string.Join(" ", line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (cleaned.Length == 0 || cleaned.Length > MaxLength) continue;
            if (result.Any(r => string.Equals(r, cleaned, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(cleaned);
            if (result.Count == Max) break;
        }
        return result;
    }
}

/// <summary>Turns a quick instruction plus selection and transcript into the model request.</summary>
public static class QuickActionRequest
{
    /// <summary>
    /// A quick instruction picked while recording applies to the marked text or, with nothing marked,
    /// to what was just dictated ("Befund diktieren, Strg+1" = structured finding in one step).
    /// Returns null when there is nothing to apply it to.
    /// </summary>
    public static (string? Selection, string Instruction)? Build(string quickAction, string? selection, string transcript)
    {
        var said = transcript?.Trim() ?? "";
        if (!string.IsNullOrEmpty(selection))
            return said.Length == 0
                ? (selection, quickAction)
                : (selection, quickAction + "\n\nZusätzlich gesagt: " + said);
        if (said.Length > 0) return (said, quickAction);
        return null;
    }
}
