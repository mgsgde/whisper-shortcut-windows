using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisperShortcut.Core.Mcp;

/// <summary>The one path that writes Settings for MCP (directly or after an accepted proposal), and the undo of it.</summary>
public sealed class McpConfigApplier
{
    private readonly Settings _settings;
    private readonly McpChangeLog _log;
    private readonly IMcpHost _host;
    private readonly Func<PromptHistory>? _history;

    /// <summary>
    /// <paramref name="history"/> is a factory, called each time a system prompt is applied (load fresh, record, save):
    /// the settings window keeps its own PromptHistory, and a long-lived instance here would overwrite its entries.
    /// </summary>
    public McpConfigApplier(Settings settings, McpChangeLog log, IReadOnlyList<McpSetting> catalog, IMcpHost host, Func<PromptHistory>? history = null)
    {
        _settings = settings;
        _log = log;
        Catalog = catalog;
        _host = host;
        _history = history;
    }

    /// <summary>The settings AI apps may change.</summary>
    public IReadOnlyList<McpSetting> Catalog { get; }

    private static string Norm(string? s) => (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static List<string> Split(string? text) =>
        string.IsNullOrEmpty(text) ? new() : text.Replace("\r\n", "\n").Split('\n').ToList();

    internal static string GlossaryTitle(int added) => $"Glossar: {added} Begriff{(added == 1 ? "" : "e")} hinzugefügt";

    /// <summary>The glossary as text, one term per line.</summary>
    public string CurrentGlossaryText() => string.Join("\n", _settings.Glossary);

    /// <summary>The stored prompt of "dictation" or "dictate_prompt" ("" = default).</summary>
    public string CurrentSystemPrompt(string section) => section switch
    {
        "dictation" => _settings.DictateSystemPrompt,
        "dictate_prompt" => _settings.PromptModeSystemPrompt,
        _ => throw new McpConfigException("Unknown section: " + section + ". Use dictation or dictate_prompt."),
    };

    /// <summary>The quick instructions as text, one per line.</summary>
    public string CurrentQuickInstructionsText() => string.Join("\n", _settings.QuickActions);

    // Save settings; on failure put the previous state back so memory and disk agree.
    private void SaveOrRestore(Action restore)
    {
        try { _settings.Save(); }
        catch (Exception ex)
        {
            restore();
            throw new McpConfigException("Saving failed: " + ex.Message);
        }
    }

    private void AfterApply()
    {
        // The change is done and saved; a failing hotkey re-registration must not turn that into an error.
        try { _host.AfterApply(); }
        catch (Exception ex) { Log.Error("MCP: AfterApply failed", ex); }
    }

    /// <summary>Replaces the glossary and records the change. Returns the change id.</summary>
    public string ApplyGlossary(IReadOnlyList<string> lines, string title, string rationale, bool viaProposal)
    {
        var previous = _settings.Glossary;
        _settings.Glossary = lines.ToList();
        SaveOrRestore(() => _settings.Glossary = previous);
        var change = _log.Record(McpChangeKind.Glossary, "glossary", title, string.Join("\n", previous), string.Join("\n", lines), rationale, viaProposal);
        AfterApply();
        return change.Id;
    }

    private static string DefaultFor(string section) =>
        section == "dictation" ? Prompts.DefaultTranscriptionSystemPrompt : Prompts.DefaultPromptModeSystemPrompt;

    private static PromptField FieldFor(string section) => section == "dictation" ? PromptField.Dictate : PromptField.PromptMode;

    private void SetPrompt(string section, string stored)
    {
        if (section == "dictation") _settings.DictateSystemPrompt = stored;
        else _settings.PromptModeSystemPrompt = stored;
    }

    private void RecordHistory(string section, string previous, string current)
    {
        if (_history == null) return;
        try
        {
            var h = _history();
            if (h.Record(FieldFor(section), previous, current, DateTime.UtcNow)) h.Save();
        }
        catch (Exception ex) { Log.Error("MCP: prompt history not saved", ex); }
    }

    /// <summary>Stores a system prompt ("default" text is stored as empty) and records the change. Returns the change id.</summary>
    public string ApplySystemPrompt(string section, string content, string rationale, bool viaProposal)
    {
        var previous = CurrentSystemPrompt(section);
        var stored = Settings.CustomOrEmpty(content, DefaultFor(section));
        SetPrompt(section, stored);
        SaveOrRestore(() => SetPrompt(section, previous));
        RecordHistory(section, previous, stored);
        var title = section == "dictation" ? "System-Prompt Diktieren geändert" : "System-Prompt Dictate Prompt geändert";
        var change = _log.Record(McpChangeKind.SystemPrompt, section, title, previous, stored, rationale, viaProposal);
        AfterApply();
        return change.Id;
    }

    /// <summary>Replaces the quick instructions and records the change. Returns the change id.</summary>
    public string ApplyQuickInstructions(IReadOnlyList<string> list, string rationale, bool viaProposal)
    {
        var previous = _settings.QuickActions;
        var cleaned = QuickActionList.Clean(list);
        _settings.QuickActions = cleaned;
        SaveOrRestore(() => _settings.QuickActions = previous);
        var change = _log.Record(McpChangeKind.QuickInstructions, "quick_instructions", "Schnellanweisungen geändert",
            string.Join("\n", previous), string.Join("\n", cleaned), rationale, viaProposal);
        AfterApply();
        return change.Id;
    }

    private McpSetting Find(string id) =>
        Catalog.FirstOrDefault(s => s.Id == id) ?? throw new McpConfigException($"Die Einstellung {id} gibt es nicht.");

    private static JsonNode? ParseValue(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { throw new McpConfigException("Ein gespeicherter Wert ist ungültig."); }
    }

    /// <summary>Writes each item's new value through the catalog, saves and records one change. Returns the change id.</summary>
    public string ApplySettings(IReadOnlyList<McpSettingsChangeItem> items, string rationale, bool viaProposal)
    {
        var written = new List<(McpSetting Setting, JsonNode? Previous)>();
        void Restore()
        {
            foreach (var (setting, prev) in Enumerable.Reverse(written))
            {
                if (prev == null) continue; // unknown before (e.g. autostart): nothing to put back
                try { setting.Write(prev); } catch (Exception ex) { Log.Error("MCP: restoring a setting failed", ex); }
            }
        }

        try
        {
            foreach (var item in items)
            {
                var setting = Find(item.Id);
                var value = ParseValue(item.ToJson);
                var previous = setting.Read();
                setting.Write(value);
                written.Add((setting, previous));
            }
        }
        catch
        {
            Restore();
            throw;
        }
        SaveOrRestore(Restore);

        var title = items.Count == 1
            ? $"Einstellung: {items[0].Title} {items[0].FromLabel} → {items[0].ToLabel}"
            : $"{items.Count} Einstellungen geändert";
        var change = _log.Record(McpChangeKind.Settings, "settings", title, null, null, rationale, viaProposal, items.ToList());
        AfterApply();
        return change.Id;
    }

    /// <summary>Restores what a change replaced, as long as nobody changed the same thing since. Does not add a change entry.</summary>
    public McpUndoResult Undo(string changeId)
    {
        var change = _log.Changes.FirstOrDefault(c => c.Id == changeId);
        if (change == null || change.UndoneUtc != null)
            return new McpUndoResult(McpUndoOutcome.NotFound, "Diese Änderung gibt es nicht oder sie wurde schon rückgängig gemacht.");

        const string Conflict = "Inzwischen wurde das von Hand geändert; nichts wurde zurückgesetzt.";
        try
        {
            switch (change.Kind)
            {
                case McpChangeKind.Glossary:
                {
                    if (Norm(CurrentGlossaryText()) != Norm(change.NewContent)) return new(McpUndoOutcome.Conflict, Conflict);
                    var current = _settings.Glossary;
                    _settings.Glossary = Split(change.PreviousContent);
                    SaveOrRestore(() => _settings.Glossary = current);
                    break;
                }
                case McpChangeKind.SystemPrompt:
                {
                    var section = change.Target;
                    var current = CurrentSystemPrompt(section);
                    if (Norm(current) != Norm(change.NewContent)) return new(McpUndoOutcome.Conflict, Conflict);
                    var restored = change.PreviousContent ?? "";
                    SetPrompt(section, restored);
                    SaveOrRestore(() => SetPrompt(section, current));
                    RecordHistory(section, current, restored);
                    break;
                }
                case McpChangeKind.QuickInstructions:
                {
                    if (Norm(CurrentQuickInstructionsText()) != Norm(change.NewContent)) return new(McpUndoOutcome.Conflict, Conflict);
                    var current = _settings.QuickActions;
                    _settings.QuickActions = QuickActionList.Clean(Split(change.PreviousContent));
                    SaveOrRestore(() => _settings.QuickActions = current);
                    break;
                }
                default:
                {
                    var skipped = new List<string>();
                    var restoredCount = 0;
                    var written = new List<(McpSetting, JsonNode?)>();
                    foreach (var item in change.SettingsChanges ?? new())
                    {
                        var setting = Catalog.FirstOrDefault(s => s.Id == item.Id);
                        var from = ParseValue(item.FromJson);
                        if (setting == null || from == null || !McpSettingsCatalog.SameValue(setting.Read(), ParseValue(item.ToJson)))
                        {
                            skipped.Add(item.Title);
                            continue;
                        }
                        written.Add((setting, setting.Read()));
                        setting.Write(from);
                        restoredCount++;
                    }
                    if (restoredCount == 0) return new(McpUndoOutcome.Conflict, Conflict);
                    SaveOrRestore(() =>
                    {
                        foreach (var (s, prev) in written)
                            if (prev != null) try { s.Write(prev); } catch { }
                    });
                    _log.MarkUndone(change.Id, DateTime.UtcNow);
                    AfterApply();
                    return skipped.Count == 0
                        ? new(McpUndoOutcome.Undone, "Rückgängig gemacht.")
                        : new(McpUndoOutcome.UndonePartial, "Teilweise rückgängig gemacht. Nicht zurückgesetzt, weil inzwischen anders eingestellt: " + string.Join(", ", skipped) + ".");
                }
            }
        }
        catch (McpConfigException ex)
        {
            return new(McpUndoOutcome.Failed, ex.Message);
        }

        _log.MarkUndone(change.Id, DateTime.UtcNow);
        AfterApply();
        return new(McpUndoOutcome.Undone, "Rückgängig gemacht.");
    }
}
