using WhisperShortcut.Core;
using System.Text.Json.Nodes;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests.Mcp;

public class McpUndoTests
{
    private static JsonObject A(string json) => McpStack.Args(json);
    private const string R = "\"rationale\":\"Weil.\"";

    private static string ChangeId(JsonObject o) => o["change_id"]!.GetValue<string>();

    [Fact]
    public void Undo_Glossary_RestoresPrevious()
    {
        using var s = new McpStack();
        s.Settings.Glossary = new() { "Alt" };
        s.Settings.Save();
        var id = ChangeId(s.ToolJson("add_glossary_terms", A($"{{\"terms\":[\"Neu\"],{R}}}")));
        var u = s.Applier.Undo(id);
        Assert.Equal(McpUndoOutcome.Undone, u.Outcome);
        Assert.Equal(new[] { "Alt" }, s.ReloadSettings().Glossary);
        Assert.NotNull(s.Changes.Changes[0].UndoneUtc);
        Assert.Single(s.Changes.Changes); // undo adds no entry
        Assert.Equal(2, s.Host.AfterApplyCount);
    }

    [Fact]
    public void Undo_AfterManualChange_IsConflict()
    {
        using var s = new McpStack();
        var id = ChangeId(s.ToolJson("add_glossary_terms", A($"{{\"terms\":[\"Neu\"],{R}}}")));
        s.Settings.Glossary.Add("Von Hand");
        var u = s.Applier.Undo(id);
        Assert.Equal(McpUndoOutcome.Conflict, u.Outcome);
        Assert.Equal("Inzwischen wurde das von Hand geändert; nichts wurde zurückgesetzt.", u.Message);
        Assert.Equal(new[] { "Neu", "Von Hand" }, s.Settings.Glossary);
        Assert.Null(s.Changes.Changes[0].UndoneUtc);
    }

    [Fact]
    public void Undo_Twice_Or_Unknown_IsNotFound()
    {
        using var s = new McpStack();
        var id = ChangeId(s.ToolJson("add_glossary_terms", A($"{{\"terms\":[\"Neu\"],{R}}}")));
        Assert.Equal(McpUndoOutcome.Undone, s.Applier.Undo(id).Outcome);
        Assert.Equal(McpUndoOutcome.NotFound, s.Applier.Undo(id).Outcome);
        Assert.Equal(McpUndoOutcome.NotFound, s.Applier.Undo("unknown").Outcome);
    }

    [Fact]
    public void Undo_Settings_Partial_NamesTheSkippedOnes()
    {
        using var s = new McpStack();
        var id = ChangeId(s.ToolJson("update_settings", A($"{{\"changes\":{{\"general.sounds\":false,\"general.auto_update_check\":false}},{R}}}")));
        s.Settings.Sounds = true; // changed by hand since
        var u = s.Applier.Undo(id);
        Assert.Equal(McpUndoOutcome.UndonePartial, u.Outcome);
        Assert.Contains("Signaltöne", u.Message);
        Assert.DoesNotContain("Automatisch nach Updates", u.Message);
        var disk = s.ReloadSettings();
        Assert.True(disk.AutoUpdateCheck);
        Assert.True(disk.Sounds);
    }

    [Fact]
    public void Undo_Settings_NothingRestorable_IsConflict_AndFullRestoreWorks()
    {
        using var s = new McpStack();
        var id = ChangeId(s.ToolJson("update_settings", A($"{{\"changes\":{{\"general.sounds\":false}},{R}}}")));
        s.Settings.Sounds = true;
        Assert.Equal(McpUndoOutcome.Conflict, s.Applier.Undo(id).Outcome);

        var id2 = ChangeId(s.ToolJson("update_settings", A($"{{\"changes\":{{\"general.sounds\":false,\"shortcuts.dictate\":\"Ctrl+Shift+Y\"}},{R}}}")));
        Assert.Equal(McpUndoOutcome.Undone, s.Applier.Undo(id2).Outcome);
        var disk = s.ReloadSettings();
        Assert.True(disk.Sounds);
        Assert.Equal("Ctrl+Shift+D", disk.DictateHotkey);
    }

    [Fact]
    public void Undo_SystemPrompt_And_QuickInstructions()
    {
        using var s = new McpStack();
        s.Settings.DictateSystemPrompt = "Alt";
        var p = s.ToolJson("update_system_prompt", A($"{{\"section\":\"dictation\",\"content\":\"Neu\",{R}}}"));
        var id = s.Proposals.Accept(p["id"]!.GetValue<string>());
        Assert.Equal("System-Prompt Diktieren geändert", s.Changes.Changes[0].Title);
        Assert.Equal(McpUndoOutcome.Undone, s.Applier.Undo(id).Outcome);
        Assert.Equal("Alt", s.ReloadSettings().DictateSystemPrompt);

        var q = ChangeId(s.ToolJson("update_quick_instructions", A($"{{\"instructions\":[\"Eins\"],{R}}}")));
        Assert.Equal(McpUndoOutcome.Undone, s.Applier.Undo(q).Outcome);
        Assert.Equal(QuickActionList.Defaults, s.ReloadSettings().QuickActions);
    }

    [Fact]
    public void ChangeLog_PersistsAcrossReload_AndKeepsAtMost200()
    {
        using var s = new McpStack();
        var id = ChangeId(s.ToolJson("add_glossary_terms", A($"{{\"terms\":[\"Neu\"],{R}}}")));
        s.Applier.Undo(id);
        var reloaded = new McpChangeLog(s.ChangesPath);
        var c = Assert.Single(reloaded.Changes);
        Assert.Equal(id, c.Id);
        Assert.Equal(McpChangeKind.Glossary, c.Kind);
        Assert.Equal("Glossar: 1 Begriff hinzugefügt", c.Title);
        Assert.Equal("Weil.", c.Rationale);
        Assert.NotNull(c.UndoneUtc);

        var log = new McpChangeLog(Path.Combine(s.Dir, "many.jsonl"));
        for (var i = 0; i < 205; i++) log.Record(McpChangeKind.Settings, "settings", "t" + i, null, null, "r", false);
        Assert.Equal(McpChangeLog.MaxChanges, new McpChangeLog(Path.Combine(s.Dir, "many.jsonl")).Changes.Count);
        Assert.Equal("t204", log.Changes[0].Title);
    }

    [Fact]
    public void ChangeLog_SkipsBadLines()
    {
        using var s = new McpStack();
        File.WriteAllText(s.ChangesPath, "not json\n{\"id\":\"a\",\"kind\":\"glossary\",\"title\":\"T\"}\n");
        var log = new McpChangeLog(s.ChangesPath);
        Assert.Equal("a", Assert.Single(log.Changes).Id);
    }
}
