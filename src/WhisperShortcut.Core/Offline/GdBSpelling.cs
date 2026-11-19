using System.Text;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// Parakeet writes the GdB token several ways ("EinzelgdB30", "Einzel GDB 20", "GDB10",
/// "GesamtgdB", "eine Einzelgdb"). One spelling: "Einzel-GdB", "Gesamt-GdB", or "GdB", and a
/// number glued on gains a space ("GdB10" → "GdB 10", "EinzelgdB30" → "Einzel-GdB 30",
/// "GesamtgdB50" → "Gesamt-GdB 50").
///
/// Whole tokens only. "gdb" inside "einzelgdb" is that token; a different word that merely
/// contains the letters ("AbGdB", "Hausgdbau") is left alone. Case-insensitive.
///
/// Runs before <see cref="GermanNumberWords.DigitizingQuantities"/>. That rule looks for the token
/// "gdb" case-insensitively, and it splits on anything that is not a letter, so "Einzel-GdB" still
/// exposes "GdB". Port of the Mac app's GdBSpelling.swift (Mac #493). The glossary casing split
/// (ParakeetVocabularyGuard) is not ported.
/// </summary>
public static class GdBSpelling
{
    /// <summary><paramref name="text"/> with those tokens rewritten. Text without one comes back unchanged.</summary>
    public static string Normalize(string text)
    {
        var matches = Pattern.Matches(text);
        if (matches.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (Match match in matches)
        {
            sb.Append(text, cursor, match.Index - cursor);
            var token = match.Groups[1].Success
                ? (match.Groups[1].Value.Equals("gesamt", StringComparison.OrdinalIgnoreCase) ? "Gesamt-GdB" : "Einzel-GdB")
                : "GdB";
            if (match.Groups[2].Success)
                sb.Append(token).Append(' ').Append(match.Groups[2].Value);
            else
                sb.Append(token);
            cursor = match.Index + match.Length;
        }
        sb.Append(text, cursor, text.Length - cursor);
        return sb.ToString();
    }

    // "einzel" / "gesamt" with an optional hyphen or space, or a bare "gdb", then digits glued on.
    private static readonly Regex Pattern = new(
        @"\b(?:(einzel|gesamt)[ \-]?)?gdb(\d+)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
