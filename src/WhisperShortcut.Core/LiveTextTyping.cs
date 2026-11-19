using System.Globalization;

namespace WhisperShortcut.Core;

/// <summary>
/// Dictate Live into Chromium browsers (Mac LiveTextTyping, #339): the renderer sometimes drops a synthesized paste,
/// and nothing on our side can see that. Plain Unicode key events reach the renderer every time, so these targets
/// get the piece typed instead of pasted (the clipboard stays untouched).
/// </summary>
public static class LiveTextTyping
{
    /// <summary>One key-event sized piece of a live piece: a text run, or a line break (Shift+Return).</summary>
    public readonly record struct Chunk(string? Text)
    {
        public bool IsNewline => Text == null;
        public static Chunk Newline => new(null);
    }

    /// <summary>A key in the typed line-break chord.</summary>
    public enum LineBreakKey { Shift, Return }

    /// <summary>One half of a key event: down, then up.</summary>
    public readonly record struct LineBreakStep(LineBreakKey Key, bool Up);

    /// <summary>
    /// Line breaks typed into Chromium (Mac #396). A bare Return submits a single-line web input and sends in
    /// chat-like boxes; Shift+Return is a line break in a textarea and does nothing in an input. Ctrl+V paste
    /// does not use this chord.
    /// </summary>
    public static readonly LineBreakStep[] LineBreakSteps =
    [
        new(LineBreakKey.Shift, false),
        new(LineBreakKey.Return, false),
        new(LineBreakKey.Return, true),
        new(LineBreakKey.Shift, true),
    ];

    /// <summary>Most UTF-16 units of text per SendInput call (Mac maxUnitsPerChunk).</summary>
    public const int MaxUnitsPerChunk = 20;

    private static readonly HashSet<string> ChromiumProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "opera", "opera_gx",
    };

    /// <summary>Process name without ".exe".</summary>
    public static bool IsChromium(string? processName) =>
        !string.IsNullOrEmpty(processName) && ChromiumProcesses.Contains(processName);

    /// <summary>
    /// Splits <paramref name="text"/> into text runs of at most 20 UTF-16 units that never cut a grapheme cluster
    /// (so never a surrogate pair), and one newline per line break (CRLF counts as one).
    /// </summary>
    public static List<Chunk> Chunks(string text)
    {
        var result = new List<Chunk>();
        var run = new System.Text.StringBuilder();
        void Flush()
        {
            if (run.Length > 0) result.Add(new Chunk(run.ToString()));
            run.Clear();
        }
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (element is "\n" or "\r\n" or "\r")
            {
                Flush();
                result.Add(Chunk.Newline);
                continue;
            }
            if (run.Length + element.Length > MaxUnitsPerChunk) Flush();
            run.Append(element);
        }
        Flush();
        return result;
    }
}
