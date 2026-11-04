using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// Turns Markdown-formatted text into something worth listening to, for Read Aloud (port of the Mac SpeechTextSanitizer).
/// Strips Markdown syntax and URLs on the way to the TTS model. Deliberately lossy where speech demands it: fenced code
/// blocks and images are dropped rather than voiced, and citation markers ([1]) disappear instead of becoming a spoken "eins".
/// </summary>
public static class SpeechTextSanitizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static Regex Rx(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.CultureInvariant, RegexTimeout);

    // Any hit keeps the rewrite on. Anchored to line starts where Markdown is line based.
    private static readonly Regex[] NonProse =
    {
        Rx("""```"""),                                                       // fenced code
        Rx("""`[^`\n]+`"""),                                                 // inline code
        Rx("""^\s{0,3}#{1,6}\s""", RegexOptions.Multiline),                  // headings
        Rx("""^\s*([-*+]|\d+[.)])\s""", RegexOptions.Multiline),             // bullet / numbered lists
        Rx("""^\s*\|.*\|\s*$""", RegexOptions.Multiline),                    // table rows
        Rx("""\*\*[^*\n]+\*\*|__[^_\n]+__"""),                               // bold
        Rx("""\[[^\]\n]*\]\([^)\n]*\)"""),                                  // links / images
        Rx("""(?i)\b(https?|ftp)://|\bwww\.[a-z0-9-]+\.[a-z]{2,}"""),        // URLs
        Rx("""(?:^|[\s(])(?:~|\.{1,2})?/[\w.-]+/[\w./-]+"""),                // file paths
        Rx("""[{}\[\];]|=>|->|::|</|/>|\$\{|\w+\(\)"""),                     // code: braces, arrows, tags, calls
        Rx("""\d{2}:\d{2}:\d{2}|\d{4}-\d{2}-\d{2}T"""),                      // log timestamps
    };

    // A selection that never closes a sentence (fragments, a table row, a heading block) is not read verbatim.
    private static readonly Regex SentenceEnd = Rx("""[.!?…]["'“”„‘’«»)\]]?(\s|$)""");

    private const string ProsePunctuation = ".,;:!?…'’‘\"“”„«»()-–—";

    private static bool IsLetterLike(Rune r)
    {
        if (Rune.IsLetter(r)) return true;
        // Foundation's CharacterSet.letters also holds the combining marks.
        var cat = Rune.GetUnicodeCategory(r);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    /// <summary>
    /// True when the selection is plain running prose, which the smart rewrite would hand back essentially unchanged after
    /// a few seconds of round trip. The gate is deliberately strict: any structural marker (Markdown, URLs, file paths,
    /// code or log shapes), a low share of letters, or text that never ends a sentence keeps the rewrite on, because a
    /// wrongly skipped rewrite means the voice reads braces and "https" aloud while a wrongly kept one costs a few seconds.
    /// </summary>
    public static bool LooksLikePlainProse(string text)
    {
        // Windows text arrives with CRLF; the line-anchored patterns below expect plain \n.
        var trimmed = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (trimmed.Length == 0) return false;

        foreach (var rx in NonProse)
            if (rx.IsMatch(trimmed)) return false;

        // Code and logs are symbol- and digit-heavy; prose is overwhelmingly letters. Ordinary sentence punctuation stays
        // out of the denominator so a short quoted sentence is not penalised for its commas and apostrophes.
        var counted = 0;
        var letters = 0;
        foreach (var r in trimmed.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r)) continue;
            if (r.IsBmp && ProsePunctuation.Contains((char)r.Value)) continue;
            counted++;
            if (IsLetterLike(r)) letters++;
        }
        if (counted == 0) return false;
        if ((double)letters / counted < 0.85) return false;

        return SentenceEnd.IsMatch(trimmed);
    }

    // Order matters: images before links (an image is a link with a !), fenced code before inline code, and link
    // unwrapping before bare URL removal, otherwise the URL inside [text](url) goes first and the leftover [text]() no
    // longer matches the link pattern.
    private static readonly (Regex Pattern, string Replacement)[] Steps =
    {
        // Fenced code blocks: unspeakable, and often longer than the prose around them.
        (Rx("""^[ \t]*```[\s\S]*?```[ \t]*$""", RegexOptions.Multiline), ""),
        // Unterminated final fence (a cut-off reply).
        (Rx("""^[ \t]*```[\s\S]*$""", RegexOptions.Multiline), ""),
        // Images: no spoken equivalent, and the alt text is usually a filename.
        (Rx("""!\[[^\]]*\]\([^)]*\)"""), ""),
        // Citation-style links whose label is just a number, [[1]](url) or [1](url): voicing them injects stray digits.
        (Rx("""\[{1,2}\s*\d+\s*\]{1,2}\([^)]*\)"""), ""),
        // Remaining inline links and reference links: keep the label, drop the target.
        (Rx("""\[([^\]]*)\]\([^)]*\)"""), "$1"),
        (Rx("""\[([^\]]*)\]\[[^\]]*\]"""), "$1"),
        // Bare citation markers left behind by a source list ([1] example.com).
        (Rx("""\[{1,2}\s*\d+\s*\]{1,2}"""), ""),
        // Leaked internal search tokens ([web:9], [x:3]).
        (Rx("""\[(?:web|x|post|news):\d+\]"""), ""),
        // Autolinks and bare URLs.
        (Rx("""<(?:https?|mailto):[^>]*>"""), ""),
        (Rx("""\b(?:https?://|www\.)\S+"""), ""),

        // Inline code and emphasis: keep the content, drop the markers. ** before * so bold is not shredded into stray asterisks.
        (Rx("""`([^`]*)`"""), "$1"),
        (Rx("""\*\*([^*]+)\*\*"""), "$1"),
        (Rx("""__([^_]+)__"""), "$1"),
        (Rx("""\*([^*\n]+)\*"""), "$1"),
        (Rx("""(?<![\w])_([^_\n]+)_(?![\w])"""), "$1"),
        (Rx("""~~([^~]+)~~"""), "$1"),

        // Block-level markers: headings, blockquotes and list bullets carry structure a listener hears as a pause anyway.
        (Rx("""^[ \t]*#{1,6}[ \t]+""", RegexOptions.Multiline), ""),
        (Rx("""^[ \t]*>[ \t]?""", RegexOptions.Multiline), ""),
        (Rx("""^[ \t]*[-*+•][ \t]+""", RegexOptions.Multiline), ""),
        (Rx("""^[ \t]*\d+[.)][ \t]+""", RegexOptions.Multiline), ""),
        // Horizontal rules and table separator rows (|---|:--:|).
        (Rx("""^[ \t]*([-*_])(?:[ \t]*\1){2,}[ \t]*$""", RegexOptions.Multiline), ""),
        (Rx("""^[ \t]*\|?[ \t]*:?-{2,}:?[ \t]*(\|[ \t]*:?-{2,}:?[ \t]*)*\|?[ \t]*$""", RegexOptions.Multiline), ""),
        // Remaining table cell separators become sentence-internal pauses.
        (Rx("""^[ \t]*\|[ \t]*""", RegexOptions.Multiline), ""),
        (Rx("""[ \t]*\|[ \t]*$"""), ""),
        (Rx("""[ \t]*\|[ \t]*"""), ", "),

        // Collapse the gaps everything above left behind.
        (Rx("""[ \t]{2,}"""), " "),
        (Rx("""[ \t]+$""", RegexOptions.Multiline), ""),
        (Rx("""\n{3,}"""), "\n\n"),
        // A line reduced to leftover punctuation (a source list that was all links).
        (Rx("""^[ \t]*[,.;:]+[ \t]*$""", RegexOptions.Multiline), ""),
        (Rx("""\n{3,}"""), "\n\n"),
    };

    /// <summary>
    /// Markdown source to plain speakable text: fenced code, images, URLs and citation markers dropped; link labels,
    /// emphasised words and list items kept without their markers.
    /// </summary>
    public static string PlainSpeech(string markdown)
    {
        // Windows text arrives with CRLF; the line-anchored patterns expect plain \n.
        var text = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var (pattern, replacement) in Steps)
            text = pattern.Replace(text, replacement);
        return text.Trim();
    }
}
