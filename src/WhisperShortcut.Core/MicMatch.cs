using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// Matches a stored WinMM microphone name against the names Windows reports now.
/// Product names are at most <see cref="WinMmNameLength"/> characters, and a USB endpoint that
/// re-enumerates gains an instance prefix ("2- ") directly after the opening parenthesis, so the
/// saved string and the current one stop being equal.
/// </summary>
public static class MicMatch
{
    /// <summary>Maximum product-name length returned by WinMM (<c>waveInGetDevCaps</c>).</summary>
    public const int WinMmNameLength = 31;

    // "N- " (one or more digits, a dash, a space) sitting directly after "(".
    private static readonly Regex InstancePrefix = new(@"\((\d+)- ", RegexOptions.CultureInvariant);

    /// <summary>
    /// Removes a Windows instance prefix that directly follows an opening parenthesis
    /// and trims trailing whitespace.
    /// </summary>
    public static string Normalize(string name) => InstancePrefix.Replace(name, "(").TrimEnd();

    /// <summary>
    /// Index of the device to use, or -1 when <paramref name="stored"/> is empty, absent,
    /// or matches more than one entry only through <see cref="Same"/>.
    /// An exact ordinal hit wins, even if other entries would also match fuzzily.
    /// </summary>
    public static int Find(string? stored, IReadOnlyList<string> names)
    {
        if (string.IsNullOrEmpty(stored)) return -1;

        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], stored, StringComparison.Ordinal))
                return i;

        int found = -1;
        for (int i = 0; i < names.Count; i++)
        {
            if (!Same(stored, names[i])) continue;
            if (found >= 0) return -1;
            found = i;
        }
        return found;
    }

    /// <summary>
    /// True when the normalized names are equal, or when the shorter one is a prefix of the longer,
    /// the raw name behind the shorter one was cut off at <see cref="WinMmNameLength"/>,
    /// and that shorter normalized text is at least 12 characters.
    /// </summary>
    private static bool Same(string a, string b)
    {
        string na = Normalize(a);
        string nb = Normalize(b);
        if (string.Equals(na, nb, StringComparison.Ordinal)) return true;
        if (na.Length == nb.Length) return false;

        string shorter, longer, rawShorter;
        if (na.Length < nb.Length)
        {
            shorter = na;
            longer = nb;
            rawShorter = a;
        }
        else
        {
            shorter = nb;
            longer = na;
            rawShorter = b;
        }

        return shorter.Length >= 12
            && rawShorter.Length >= WinMmNameLength
            && longer.StartsWith(shorter, StringComparison.Ordinal);
    }
}
