using System.Text;

namespace WhisperShortcut.Core;

/// <summary>
/// Turns the Glossary into Parakeet "hotwords" (contextual biasing in sherpa-onnx) so offline
/// dictation spells names and terms the way the user wants.
/// </summary>
public static class ParakeetHotwords
{
    /// <summary>
    /// Boost per hotword. Measured with the Glossary terms: 2.5 fixes the spelling of names without
    /// touching normal sentences, 3.0 already rewrote ordinary words.
    /// </summary>
    public const float Score = 2.5f;

    /// <summary>Upper bound so a huge Glossary cannot make beam search slow.</summary>
    public const int MaxTerms = 500;

    /// <summary>
    /// One hotword per Glossary line: the name before a note like <c>Bachata (not "Batchata")</c>,
    /// whitespace collapsed, duplicates (case-insensitive) dropped, order kept.
    /// </summary>
    public static IReadOnlyList<string> FromGlossary(IEnumerable<string> glossary)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in glossary)
        {
            if (result.Count >= MaxTerms) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var note = line.IndexOf(" (", StringComparison.Ordinal);
            var term = note >= 0 ? line[..note] : line;
            // sherpa-onnx reads ':' as a per-line score, so it must not appear in the term.
            term = term.Replace(':', ' ');
            term = string.Join(' ', term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (term.Length == 0 || term.Length > GlossaryEdit.MaxTermLength) continue;
            if (seen.Add(term)) result.Add(term);
        }
        return result;
    }

    /// <summary>
    /// The Parakeet model ships no bpe.vocab, which sherpa-onnx needs to encode hotwords. Derives it
    /// from tokens.txt (<c>piece id</c>) as <c>piece TAB -id</c> lines.
    /// </summary>
    public static void WriteBpeVocab(string tokensPath, string vocabPath)
    {
        var sb = new StringBuilder();
        foreach (var raw in File.ReadLines(tokensPath, Encoding.UTF8))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var space = line.LastIndexOf(' ');
            if (space <= 0 || !int.TryParse(line[(space + 1)..], out var id)) continue;
            sb.Append(line, 0, space).Append('\t').Append(-id).Append('\n');
        }
        File.WriteAllText(vocabPath, sb.ToString(), new UTF8Encoding(false));
    }
}
