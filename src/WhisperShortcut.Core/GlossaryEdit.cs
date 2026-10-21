using System.Text;

namespace WhisperShortcut.Core;

/// <summary>Outcome of trying to append the current selection to the glossary.</summary>
public enum GlossaryAddResult
{
    /// <summary>The term was appended.</summary>
    Added,
    /// <summary>The term, or the name before a parenthetical note, is already in the list.</summary>
    Duplicate,
    /// <summary>Nothing usable was left after trimming.</summary>
    Empty,
    /// <summary>Longer than <see cref="GlossaryEdit.MaxTermLength"/>: a sentence, not a glossary term.</summary>
    TooLong,
    /// <summary>The selection still contains a line break, so it is more than one term.</summary>
    MultiLine,
}

/// <summary>
/// Turns a selection into one glossary line. Copied names usually carry the surrounding
/// quotes and the sentence's final period; those are decoration, not part of the spelling.
/// </summary>
public static class GlossaryEdit
{
    /// <summary>A glossary entry is a word or a name. Longer than this, the selection is a sentence.</summary>
    public const int MaxTermLength = 60;

    private static readonly (char Open, char Close)[] QuotePairs =
    {
        ('\u201E', '\u201C'), // „ “
        ('"', '"'),
        ('\'', '\''),
        ('\u00AB', '\u00BB'), // « »
        ('\u201A', '\u2018'), // ‚ ‘
    };

    /// <summary>
    /// Appends the cleaned selection to <paramref name="glossary"/>, or leaves the list untouched.
    /// <paramref name="term"/> is the cleaned term in every case, including when nothing is added.
    /// </summary>
    public static GlossaryAddResult TryAdd(List<string> glossary, string? selection, out string term)
    {
        term = Clean(selection);
        // Collapse would hide the line break, and a multi-line selection is several terms.
        if (term.IndexOfAny('\r', '\n') >= 0) return GlossaryAddResult.MultiLine;
        term = Collapse(term);
        if (term.Length == 0) return GlossaryAddResult.Empty;
        if (term.Length > MaxTermLength) return GlossaryAddResult.TooLong;
        if (IsDuplicate(glossary, term)) return GlossaryAddResult.Duplicate;
        glossary.Add(term);
        return GlossaryAddResult.Added;
    }

    private static string Clean(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection)) return "";
        var s = selection.Trim();
        var removedPunct = false;
        while (s.Length > 0)
        {
            var unquoted = StripOneQuotePair(s);
            if (unquoted.Length != s.Length)
            {
                s = unquoted.Trim();
                continue;
            }
            // One trailing mark only, so a typed "Gödde..." keeps every period but the last.
            // Repeated after a quote strip so „Gödde“. loses the period and the quotes.
            if (!removedPunct && s[^1] is '.' or ',' or ';' or ':')
            {
                removedPunct = true;
                s = s[..^1].Trim();
                continue;
            }
            break;
        }
        return s;
    }

    private static string StripOneQuotePair(string s)
    {
        if (s.Length < 2) return s;
        foreach (var (open, close) in QuotePairs)
        {
            if (s[0] == open && s[^1] == close) return s[1..^1];
        }
        return s;
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Lines like <c>Bachata (not "Batchata")</c> already teach that spelling. The name before
    /// " (" is the term; the note is only there for the model.
    /// </summary>
    private static bool IsDuplicate(List<string> glossary, string term)
    {
        foreach (var line in glossary)
        {
            if (string.Equals(line, term, StringComparison.CurrentCultureIgnoreCase)) return true;
            var note = line.IndexOf(" (", StringComparison.Ordinal);
            if (note >= 0 && string.Equals(line[..note], term, StringComparison.CurrentCultureIgnoreCase))
                return true;
        }
        return false;
    }
}
