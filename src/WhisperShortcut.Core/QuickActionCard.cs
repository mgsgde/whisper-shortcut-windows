using System.Text.Json;

namespace WhisperShortcut.Core;

/// <summary>
/// Which quick instructions the Dictate Prompt card shows: like the Mac (QuickActionStore.combined, cardEntries = 3),
/// the three most used of the user's own in the last 30 days, own order breaking ties, listed in the Settings order.
/// Strg+1–3 pick the rows as shown, so a prompt's key can change when usage changes, its row order never does.
/// </summary>
public static class QuickActionCard
{
    /// <summary>Rows on the card (Mac <c>QuickActionStore.cardEntries</c>).</summary>
    public const int Entries = 3;

    /// <summary>"Strg+1 bis Strg+3", for texts that name the card's keys.</summary>
    public static string KeyRange => $"Strg+1 bis Strg+{Entries}";

    public static readonly TimeSpan Lookback = TimeSpan.FromDays(30);

    /// <summary>The rows for <paramref name="own"/> (Settings order) given uses per <see cref="NormalizeKey"/>.</summary>
    public static IReadOnlyList<string> Select(IReadOnlyList<string> own, IReadOnlyDictionary<string, int> counts)
    {
        var chosen = own
            .Select((text, index) => (text, index, uses: counts.TryGetValue(NormalizeKey(text), out var n) ? n : 0))
            .OrderByDescending(x => x.uses).ThenBy(x => x.index)
            .Take(Entries)
            .Select(x => x.index)
            .ToHashSet();
        return own.Where((_, index) => chosen.Contains(index)).ToList();
    }

    private static readonly HashSet<string> Fillers = new() { "ähm", "äh", "ehm", "um", "uh" };
    private static readonly char[] EdgePunctuation = { '.', ',', '!', '?', ';', ':' };

    /// <summary>Port of <c>QuickActionStore.normalizeKey</c>: lower case, fillers and edge punctuation dropped.</summary>
    public static string NormalizeKey(string? raw)
    {
        var tokens = (raw ?? "").ToLowerInvariant().Trim().TrimEnd(EdgePunctuation)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.TrimEnd(EdgePunctuation))
            .Where(t => t.Length > 0 && !Fillers.Contains(t));
        return string.Join(" ", tokens);
    }
}

/// <summary>
/// When each quick instruction was run, kept for <see cref="QuickActionCard.Lookback"/> in a small JSON file under
/// %LOCALAPPDATA%. The Mac counts the same from its interaction log; Windows has none, so it counts the picks itself.
/// </summary>
public sealed class QuickActionUsage
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<Use>? _uses;

    private sealed record Use(string Key, DateTime Utc);

    public QuickActionUsage(string? path = null) =>
        _path = path ?? Path.Combine(AppData.LocalFolder, "quick-usage.json");

    /// <summary>Uses per normalized key within the lookback window.</summary>
    public IReadOnlyDictionary<string, int> Counts(DateTime nowUtc)
    {
        lock (_lock)
        {
            return Load().Where(u => nowUtc - u.Utc <= QuickActionCard.Lookback)
                .GroupBy(u => u.Key).ToDictionary(g => g.Key, g => g.Count());
        }
    }

    /// <summary>Counts one run of <paramref name="text"/> and drops uses older than the window.</summary>
    public void Record(string text, DateTime nowUtc)
    {
        var key = QuickActionCard.NormalizeKey(text);
        if (key.Length == 0) return;
        lock (_lock)
        {
            var uses = Load();
            uses.RemoveAll(u => nowUtc - u.Utc > QuickActionCard.Lookback);
            uses.Add(new Use(key, nowUtc));
            if (AppData.ResetDone) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(uses));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Quick action usage not saved: {e.Message}");
            }
        }
    }

    private List<Use> Load()
    {
        if (_uses != null) return _uses;
        try
        {
            _uses = File.Exists(_path) ? JsonSerializer.Deserialize<List<Use>>(File.ReadAllText(_path)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Info($"Quick action usage unreadable, starting empty: {e.Message}");
            _uses = new();
        }
        return _uses;
    }
}
