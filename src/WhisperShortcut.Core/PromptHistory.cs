using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperShortcut.Core;

/// <summary>Which prompt field a version belongs to.</summary>
public enum PromptField { Dictate, PromptMode, Template }

/// <summary>One earlier text of a field. <paramref name="ReplacedAt"/> is the UTC time it stopped being the saved text.</summary>
public sealed record PromptVersion(string Text, DateTime ReplacedAt);

/// <summary>
/// A safety net for the user's custom prompts: when a saved prompt changes, the previous text is kept
/// (newest first, <see cref="Capacity"/> per field) so the settings window can offer it again.
/// Stored next to settings.json, so "Zurücksetzen" removes it with the rest.
/// </summary>
public sealed class PromptHistory
{
    /// <summary>How many earlier texts are kept per field.</summary>
    public const int Capacity = 10;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<PromptField, List<PromptVersion>> _lists = new()
    {
        [PromptField.Dictate] = new(),
        [PromptField.PromptMode] = new(),
        [PromptField.Template] = new(),
    };

    private PromptHistory(string path) => _path = path;

    /// <summary>Default location: prompt-history.json in <see cref="AppData.SettingsFolder"/>.</summary>
    public static string DefaultPath => Path.Combine(AppData.SettingsFolder, "prompt-history.json");

    /// <summary>Loads the history; a missing, empty or corrupt file yields an empty history.</summary>
    public static PromptHistory Load(string path)
    {
        var h = new PromptHistory(path);
        try
        {
            if (!File.Exists(path)) return h;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), Json);
            if (dto == null) return h;
            Fill(h._lists[PromptField.Dictate], dto.Dictate);
            Fill(h._lists[PromptField.PromptMode], dto.PromptMode);
            Fill(h._lists[PromptField.Template], dto.Template);
        }
        catch (Exception ex)
        {
            Log.Error("prompt-history.json unreadable, starting empty", ex);
            foreach (var list in h._lists.Values) list.Clear();
        }
        return h;
    }

    private static void Fill(List<PromptVersion> target, List<Entry>? source)
    {
        if (source == null) return;
        foreach (var e in source)
        {
            if (string.IsNullOrWhiteSpace(e?.Text)) continue;
            if (target.Count >= Capacity) break;
            target.Add(new PromptVersion(e.Text, DateTime.SpecifyKind(e.ReplacedAt.ToUniversalTime(), DateTimeKind.Utc)));
        }
    }

    /// <summary>Earlier texts of <paramref name="field"/>, newest first; empty when there are none.</summary>
    public IReadOnlyList<PromptVersion> Versions(PromptField field) => _lists[field];

    /// <summary>
    /// Notes that <paramref name="previous"/> was replaced by <paramref name="current"/> at <paramref name="nowUtc"/>.
    /// Texts compare with "\n" line endings and trimmed. Unchanged text does nothing. An entry equal to
    /// <paramref name="current"/> is removed (it is the saved text now). An empty previous text is not kept (empty
    /// means "default"). An identical earlier entry moves to the top; the oldest past <see cref="Capacity"/> goes.
    /// Returns true when the list changed, i.e. the caller should <see cref="Save"/>.
    /// </summary>
    public bool Record(PromptField field, string? previous, string? current, DateTime nowUtc)
    {
        var prev = Normalize(previous);
        var cur = Normalize(current);
        if (prev == cur) return false;

        var list = _lists[field];
        var changed = list.RemoveAll(v => v.Text == cur) > 0;
        if (prev.Length == 0) return changed;

        list.RemoveAll(v => v.Text == prev);
        list.Insert(0, new PromptVersion(prev, nowUtc));
        if (list.Count > Capacity) list.RemoveRange(Capacity, list.Count - Capacity);
        return true;
    }

    private static string Normalize(string? text) => (text ?? "").ReplaceLineEndings("\n").Trim();

    /// <summary>Writes the history to the path given at <see cref="Load"/>.</summary>
    public void Save()
    {
        if (AppData.ResetDone) return; // after "Zurücksetzen" nothing may bring the data folder back
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dto = new Dto
        {
            Dictate = ToEntries(PromptField.Dictate),
            PromptMode = ToEntries(PromptField.PromptMode),
            Template = ToEntries(PromptField.Template),
        };
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private List<Entry> ToEntries(PromptField field) =>
        _lists[field].Select(v => new Entry { Text = v.Text, ReplacedAt = v.ReplacedAt }).ToList();

    private sealed class Entry
    {
        public string? Text { get; set; }
        public DateTime ReplacedAt { get; set; }
    }

    private sealed class Dto
    {
        [JsonPropertyName("Dictate")] public List<Entry>? Dictate { get; set; }
        [JsonPropertyName("PromptMode")] public List<Entry>? PromptMode { get; set; }
        [JsonPropertyName("Template")] public List<Entry>? Template { get; set; }
    }
}
