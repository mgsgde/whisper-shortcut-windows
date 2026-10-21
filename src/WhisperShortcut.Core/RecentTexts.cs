using System.Text;

namespace WhisperShortcut.Core;

/// <summary>
/// The last few dictations, newest first, held only in memory. In a medical practice a dictation
/// must not linger in a file after the app exits.
/// </summary>
public sealed class RecentTexts
{
    /// <summary>How many texts the tray menu keeps.</summary>
    public const int Capacity = 5;

    // A space in this tail is a gap between words; cutting there keeps a menu item from ending mid-word.
    private const int WordBoundaryLookback = 15;

    private readonly List<string> _items = new();

    /// <summary>Newest first. Empty after <see cref="Clear"/> and before the first <see cref="Add"/>.</summary>
    public IReadOnlyList<string> Items => _items;

    /// <summary>The newest text, or null when the list is empty.</summary>
    public string? Latest => _items.Count == 0 ? null : _items[0];

    /// <summary>
    /// Remembers <paramref name="text"/>. Null and whitespace are ignored. An identical text already
    /// in the list moves to the top instead of being stored twice. The oldest entry past
    /// <see cref="Capacity"/> is dropped.
    /// </summary>
    public void Add(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var existing = _items.IndexOf(text);
        if (existing >= 0) _items.RemoveAt(existing);
        _items.Insert(0, text);
        if (_items.Count > Capacity) _items.RemoveAt(_items.Count - 1);
    }

    /// <summary>Forgets every remembered text.</summary>
    public void Clear() => _items.Clear();

    /// <summary>
    /// One menu line: every whitespace run, including newlines, becomes a single space.
    /// Longer than <paramref name="maxChars"/> is cut there and marked with "…". A word boundary
    /// in the last 15 characters of that prefix wins over a hard cut.
    /// </summary>
    public static string Preview(string text, int maxChars = 60)
    {
        var line = OneLine(text);
        if (maxChars <= 0 || line.Length <= maxChars) return maxChars <= 0 ? "" : line;
        var window = Math.Min(WordBoundaryLookback, maxChars);
        var space = line.LastIndexOf(' ', maxChars - 1, window);
        var cut = space > 0 ? space : maxChars;
        return line[..cut].TrimEnd() + "…";
    }

    private static string OneLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
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
}
