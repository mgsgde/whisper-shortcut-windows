using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperShortcut.Core.Mcp;

/// <summary>What a recorded change touched.</summary>
public enum McpChangeKind
{
    /// <summary>Glossary terms.</summary>
    Glossary,
    /// <summary>A system prompt section.</summary>
    SystemPrompt,
    /// <summary>The quick instruction list.</summary>
    QuickInstructions,
    /// <summary>One or more catalog settings.</summary>
    Settings,
}

/// <summary>One setting inside a settings change or proposal.</summary>
/// <param name="Id">Catalog id.</param>
/// <param name="Title">German title.</param>
/// <param name="FromLabel">German label of the old value.</param>
/// <param name="ToLabel">German label of the new value.</param>
/// <param name="FromJson">Old value as JSON text (replayed by undo).</param>
/// <param name="ToJson">New value as JSON text (replayed by apply).</param>
public sealed record McpSettingsChangeItem(string Id, string Title, string FromLabel, string ToLabel, string FromJson, string ToJson);

/// <summary>One applied change, kept so the user can see and undo what AI apps did.</summary>
public sealed class McpChange
{
    /// <summary>Unique id.</summary>
    public string Id { get; init; } = "";
    /// <summary>When it was applied.</summary>
    public DateTime AppliedUtc { get; init; }
    /// <summary>What it touched.</summary>
    public McpChangeKind Kind { get; init; }
    /// <summary>"glossary", "dictation"/"dictate_prompt", "quick_instructions" or "settings".</summary>
    public string Target { get; init; } = "";
    /// <summary>German one-liner for the change list.</summary>
    public string Title { get; init; } = "";
    /// <summary>Content before (glossary and quick instructions: lines joined with "\n"; system prompt: stored text, "" = default).</summary>
    public string? PreviousContent { get; init; }
    /// <summary>Content after, same format.</summary>
    public string? NewContent { get; init; }
    /// <summary>Why the AI app said it helps.</summary>
    public string Rationale { get; init; } = "";
    /// <summary>True when the user accepted a proposal first.</summary>
    public bool ViaProposal { get; init; }
    /// <summary>Set once undone.</summary>
    public DateTime? UndoneUtc { get; set; }
    /// <summary>The items of a settings change.</summary>
    public List<McpSettingsChangeItem>? SettingsChanges { get; init; }
}

/// <summary>Result of an undo.</summary>
public enum McpUndoOutcome
{
    /// <summary>Everything was restored.</summary>
    Undone,
    /// <summary>Some settings were restored, the rest had been changed by hand.</summary>
    UndonePartial,
    /// <summary>The target was changed since; nothing was restored.</summary>
    Conflict,
    /// <summary>Unknown or already undone.</summary>
    NotFound,
    /// <summary>Saving failed.</summary>
    Failed,
}

/// <summary>Outcome plus a German message for the UI.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">German text for the user.</param>
public sealed record McpUndoResult(McpUndoOutcome Outcome, string Message);

/// <summary>The change list behind "Was KI-Apps geändert haben": JSONL, newest changes first in memory, oldest first on disk.</summary>
public sealed class McpChangeLog
{
    /// <summary>Changes kept; older ones are dropped.</summary>
    public const int MaxChanges = 200;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<McpChange> _changes = new(); // oldest first

    /// <summary>Default location: mcp-changes.jsonl in <see cref="AppData.SettingsFolder"/>.</summary>
    public static string DefaultPath => Path.Combine(AppData.SettingsFolder, "mcp-changes.jsonl");

    /// <summary>Loads the file; unreadable lines are skipped.</summary>
    public McpChangeLog(string path)
    {
        _path = path;
        _changes = McpJsonl.Load<McpChange>(path, Json, c => !string.IsNullOrEmpty(c.Id));
        if (_changes.Count > MaxChanges) _changes.RemoveRange(0, _changes.Count - MaxChanges);
    }

    /// <summary>Raised after a change was recorded or undone (on the calling thread).</summary>
    public event Action? Changed;

    /// <summary>All changes, newest first.</summary>
    public IReadOnlyList<McpChange> Changes
    {
        get { lock (_gate) return Enumerable.Reverse(_changes).ToList(); }
    }

    /// <summary>Records a change that was just applied. Never throws on disk errors (the change itself is done).</summary>
    public McpChange Record(McpChangeKind kind, string target, string title, string? previous, string? @new, string rationale, bool viaProposal, List<McpSettingsChangeItem>? settingsChanges = null)
    {
        var change = new McpChange
        {
            Id = Guid.NewGuid().ToString("N"),
            AppliedUtc = DateTime.UtcNow,
            Kind = kind,
            Target = target,
            Title = title,
            PreviousContent = previous,
            NewContent = @new,
            Rationale = rationale,
            ViaProposal = viaProposal,
            SettingsChanges = settingsChanges,
        };
        lock (_gate)
        {
            _changes.Add(change);
            if (_changes.Count > MaxChanges) _changes.RemoveRange(0, _changes.Count - MaxChanges);
            Persist();
        }
        // Lengths only, never content.
        Log.Info($"MCP: change recorded kind={kind} target={target} previous={previous?.Length ?? 0} new={@new?.Length ?? 0} viaProposal={viaProposal}");
        Changed?.Invoke();
        return change;
    }

    /// <summary>Marks a change as undone (used by <see cref="McpConfigApplier.Undo"/>).</summary>
    internal void MarkUndone(string id, DateTime utc)
    {
        lock (_gate)
        {
            var c = _changes.FirstOrDefault(x => x.Id == id);
            if (c == null) return;
            c.UndoneUtc = utc;
            Persist();
        }
        Changed?.Invoke();
    }

    private void Persist()
    {
        try { McpJsonl.Save(_path, _changes, Json); }
        catch (Exception ex) { Log.Error("mcp-changes.jsonl could not be saved", ex); }
    }
}

/// <summary>Shared JSONL load/save for the change log and the proposal store.</summary>
internal static class McpJsonl
{
    public static List<T> Load<T>(string path, JsonSerializerOptions options, Func<T, bool> valid) where T : class
    {
        var list = new List<T>();
        try
        {
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<T>(line, options);
                    if (item != null && valid(item)) list.Add(item);
                }
                catch (JsonException) { /* bad line skipped */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("MCP: " + Path.GetFileName(path) + " unreadable", ex);
        }
        return list;
    }

    public static void Save<T>(string path, IEnumerable<T> items, JsonSerializerOptions options)
    {
        if (AppData.ResetDone) return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, items.Select(i => JsonSerializer.Serialize(i, options)));
        File.Move(tmp, path, overwrite: true);
    }
}
