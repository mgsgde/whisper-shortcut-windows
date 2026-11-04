using System.Text;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>How a piece of a chat answer is drawn.</summary>
public enum ChatSpanStyle { Plain, Bold, Code, CodeBlock, Heading }

/// <summary>One run of text with one style; line breaks are part of the text.</summary>
public sealed record ChatSpan(string Text, ChatSpanStyle Style);

/// <summary>
/// The Markdown subset the chat window draws (Mac ChatView renders full Markdown): headings, bullets, **bold**, `code`,
/// fenced code blocks and [links](url), which become "text (url)" so the window can make the address clickable.
/// Everything else stays as written.
/// </summary>
public static partial class ChatMarkdown
{
    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.*)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex Inline();

    /// <summary>Splits <paramref name="markdown"/> into styled runs, in order.</summary>
    public static IReadOnlyList<ChatSpan> Parse(string? markdown)
    {
        var spans = new List<ChatSpan>();
        var lines = (markdown ?? "").ReplaceLineEndings("\n").Split('\n');
        bool fence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var newline = i < lines.Length - 1 ? "\n" : "";
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
                continue; // the fence line itself (with its language tag) is not shown
            }
            if (fence)
            {
                Add(spans, line + "\n", ChatSpanStyle.CodeBlock);
                continue;
            }
            if (HeadingLine().Match(line) is { Success: true } h)
            {
                Add(spans, h.Groups[1].Value.Trim().Trim('*') + newline, ChatSpanStyle.Heading);
                continue;
            }
            if (BulletLine().Match(line) is { Success: true } b)
            {
                Add(spans, b.Groups[1].Value + "• ", ChatSpanStyle.Plain);
                line = b.Groups[2].Value;
            }
            AddInline(spans, line);
            if (newline.Length > 0) Add(spans, newline, ChatSpanStyle.Plain);
        }
        return spans;
    }

    /// <summary>The plain text the spans show (for tests and copying).</summary>
    public static string PlainText(IReadOnlyList<ChatSpan> spans) => string.Concat(spans.Select(s => s.Text));

    /// <summary>
    /// The content of the last fenced block (Mac: the copy button on a ```markdown block holds the paste-ready text), or null.
    /// An unclosed block at the end counts too: a streamed answer may still be open.
    /// </summary>
    public static string? LastCodeBlock(string? markdown)
    {
        string? last = null;
        StringBuilder? current = null;
        foreach (var line in (markdown ?? "").ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (current == null) current = new StringBuilder();
                else
                {
                    last = current.ToString().TrimEnd('\n');
                    current = null;
                }
                continue;
            }
            current?.Append(line).Append('\n');
        }
        if (current != null && current.Length > 0) last = current.ToString().TrimEnd('\n');
        return last;
    }

    private static void AddInline(List<ChatSpan> spans, string text)
    {
        int at = 0;
        foreach (Match m in Inline().Matches(text))
        {
            if (m.Index > at) Add(spans, text[at..m.Index], ChatSpanStyle.Plain);
            if (m.Groups[1].Success) Add(spans, m.Groups[1].Value, ChatSpanStyle.Bold);
            else if (m.Groups[2].Success) Add(spans, m.Groups[2].Value, ChatSpanStyle.Code);
            else Add(spans, $"{m.Groups[3].Value} ({m.Groups[4].Value})", ChatSpanStyle.Plain);
            at = m.Index + m.Length;
        }
        if (at < text.Length) Add(spans, text[at..], ChatSpanStyle.Plain);
    }

    // Neighbouring runs of the same style merge, so the window sets fonts as rarely as possible.
    private static void Add(List<ChatSpan> spans, string text, ChatSpanStyle style)
    {
        if (text.Length == 0) return;
        if (spans.Count > 0 && spans[^1].Style == style) spans[^1] = spans[^1] with { Text = spans[^1].Text + text };
        else spans.Add(new ChatSpan(text, style));
    }
}
