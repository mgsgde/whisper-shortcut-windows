using System.Text.Json.Nodes;
using WhisperShortcut.Core;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests.Mcp;

public class McpToolsTests
{
    private static JsonObject A(string json) => McpStack.Args(json);
    private const string R = "\"rationale\":\"Weil der Nutzer es diktiert.\"";

    [Fact]
    public void ReadToken_CannotWrite()
    {
        using var s = new McpStack();
        var r = s.Tool("add_glossary_terms", A($"{{\"terms\":[\"Gödde\"],{R}}}"), McpAccess.Read);
        Assert.True(r.IsError);
        Assert.Contains("can only read", r.Text);
        Assert.Empty(s.Settings.Glossary);
        Assert.True(s.Tool("get_proposal_status", A("{\"id\":\"x\"}"), McpAccess.Read).IsError);
    }

    [Fact]
    public void AccessLevelRead_And_HostBlock_PreventWrites()
    {
        using var s = new McpStack(McpAccessLevel.Read);
        var r = s.Tool("add_glossary_terms", A($"{{\"terms\":[\"Gödde\"],{R}}}"));
        Assert.True(r.IsError);
        Assert.Contains("is off", r.Text);
        Assert.Empty(s.Settings.Glossary);

        s.Config.AccessLevel = McpAccessLevel.Apply;
        s.Host.BlockedReason = "A recording is running.";
        var b = s.Tool("add_glossary_terms", A($"{{\"terms\":[\"Gödde\"],{R}}}"));
        Assert.True(b.IsError);
        Assert.Equal("A recording is running.", b.Text);
        Assert.Empty(s.Settings.Glossary);
    }

    [Fact]
    public void AddGlossaryTerms_Apply_SavesToDisk_AndCallsAfterApply()
    {
        using var s = new McpStack();
        var o = s.ToolJson("add_glossary_terms", A($"{{\"terms\":[\"Gödde\",\"„Kubernetes“\",\"Tessa (not \\\"Tesa\\\", \\\"Dessa\\\")\"],{R}}}"));
        Assert.Equal("applied", o["status"]!.GetValue<string>());
        Assert.Equal(3, o["added"]!.AsArray().Count);
        Assert.Equal(3, o["glossary_count"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(o["change_id"]!.GetValue<string>()));
        Assert.Equal(1, s.Host.AfterApplyCount);
        var disk = s.ReloadSettings();
        Assert.Equal(new[] { "Gödde", "Kubernetes", "Tessa (not \"Tesa\", \"Dessa\")" }, disk.Glossary);
        Assert.Equal("Glossar: 3 Begriffe hinzugefügt", s.Changes.Changes[0].Title);
    }

    [Fact]
    public void AddGlossaryTerms_DedupesByFold_AndUpgradesBareLine()
    {
        using var s = new McpStack();
        s.Settings.Glossary = new() { "Gödde", "Tessa" };
        s.Settings.Save();
        var same = s.ToolJson("add_glossary_terms", A($"{{\"terms\":[\"godde\",\"GÖDDE\",\"Tessa\"],{R}}}"));
        Assert.Equal("no_change", same["status"]!.GetValue<string>());
        Assert.Equal(0, s.Host.AfterApplyCount);

        var up = s.ToolJson("add_glossary_terms", A($"{{\"terms\":[\"Tessa (nicht \\\"Tesa\\\")\"],{R}}}"));
        Assert.Equal("applied", up["status"]!.GetValue<string>());
        Assert.Equal(new[] { "Gödde", "Tessa (not \"Tesa\")" }, s.ReloadSettings().Glossary);
        // An annotated line is never replaced.
        var again = s.ToolJson("add_glossary_terms", A($"{{\"terms\":[\"Tessa (not \\\"Dessa\\\")\"],{R}}}"));
        Assert.Equal("no_change", again["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("a, b")]
    [InlineData("eins zwei drei vier fünf sechs")]
    [InlineData("Zeile\\nzwei")]
    [InlineData("   ")]
    public void AddGlossaryTerms_InvalidTerm_IsRejected(string term)
    {
        using var s = new McpStack();
        var r = s.Tool("add_glossary_terms", A($"{{\"terms\":[\"{term}\"],{R}}}"));
        Assert.True(r.IsError);
        Assert.Empty(s.Settings.Glossary);
    }

    [Fact]
    public void AddGlossaryTerms_TooLongTerm_AndBudget_AreRejected()
    {
        using var s = new McpStack();
        Assert.True(s.Tool("add_glossary_terms", A($"{{\"terms\":[\"{new string('x', 61)}\"],{R}}}")).IsError);
        s.Settings.Glossary = Enumerable.Range(0, 100).Select(i => new string('a', 58) + i.ToString("D2")).ToList(); // 6000 chars
        var r = s.Tool("add_glossary_terms", A($"{{\"terms\":[\"Neu\"],{R}}}"));
        Assert.True(r.IsError);
        Assert.Contains("0 are left", r.Text);
    }

    [Fact]
    public void Rationale_IsRequired()
    {
        using var s = new McpStack();
        var r = s.Tool("add_glossary_terms", A("{\"terms\":[\"Gödde\"]}"));
        Assert.True(r.IsError);
        Assert.Contains("rationale", r.Text);
        var longR = s.Tool("add_glossary_terms", A($"{{\"terms\":[\"Gödde\"],\"rationale\":\"{new string('x', 301)}\"}}"));
        Assert.True(longR.IsError);
        Assert.Empty(s.Settings.Glossary);
    }

    [Fact]
    public void ProposeLevel_CreatesPending_AndChangesNothing()
    {
        using var s = new McpStack(McpAccessLevel.Propose);
        var o = s.ToolJson("add_glossary_terms", A($"{{\"terms\":[\"Gödde\"],{R}}}"));
        Assert.Equal("pending_review", o["status"]!.GetValue<string>());
        Assert.Single(s.Host.Proposals);
        Assert.Equal(o["id"]!.GetValue<string>(), s.Host.Proposals[0].Id);
        Assert.Empty(s.Settings.Glossary);
        Assert.Equal(0, s.Host.AfterApplyCount);
        var st = s.ToolJson("get_proposal_status", A($"{{\"id\":\"{o["id"]}\"}}"));
        Assert.Equal("pending", st["status"]!.GetValue<string>());

        var id = s.Proposals.Accept(s.Host.Proposals[0].Id);
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(new[] { "Gödde" }, s.ReloadSettings().Glossary);
        Assert.Equal("accepted", s.ToolJson("get_proposal_status", A($"{{\"id\":\"{o["id"]}\"}}"))["status"]!.GetValue<string>());
        Assert.True(s.Tool("get_proposal_status", A("{\"id\":\"nope\"}")).IsError);
    }

    [Fact]
    public void SystemPrompt_IsAlwaysAProposal_AndAcceptStoresIt()
    {
        using var s = new McpStack(McpAccessLevel.Apply);
        var o = s.ToolJson("update_system_prompt", A($"{{\"section\":\"dictation\",\"content\":\"Schreibe Zahlen als Ziffern.\",{R}}}"));
        Assert.Equal("pending_review", o["status"]!.GetValue<string>());
        Assert.Equal("", s.Settings.DictateSystemPrompt);
        s.Proposals.Accept(o["id"]!.GetValue<string>());
        Assert.Equal("Schreibe Zahlen als Ziffern.", s.ReloadSettings().DictateSystemPrompt);
        Assert.Equal(1, s.Host.AfterApplyCount);
    }

    [Fact]
    public void SystemPrompt_DefaultText_IsStoredAsEmpty_AndHistoryKeepsTheOldOne()
    {
        using var s = new McpStack();
        s.Settings.PromptModeSystemPrompt = "Eigener Text";
        s.Settings.Save();
        // The default text is sent through the proposal store directly here.
        var o = s.Proposals.Create(McpProposalKind.SystemPrompt, "t", "r", "dictate_prompt", "Eigener Text", Prompts.DefaultPromptModeSystemPrompt);
        s.Proposals.Accept(o.Id);
        Assert.Equal("", s.ReloadSettings().PromptModeSystemPrompt);
        Assert.Equal("Eigener Text", PromptHistory.Load(s.HistoryPath).Versions(PromptField.PromptMode)[0].Text);
    }

    [Fact]
    public void SystemPrompt_SameAsCurrent_IsNoChange_AndBadInputRejected()
    {
        using var s = new McpStack();
        var same = s.ToolJson("update_system_prompt", A($"{{\"section\":\"dictation\",\"content\":{JsonValue.Create(Prompts.DefaultTranscriptionSystemPrompt)!.ToJsonString()},{R}}}"));
        Assert.Equal("no_change", same["status"]!.GetValue<string>());
        Assert.True(s.Tool("update_system_prompt", A($"{{\"section\":\"x\",\"content\":\"a\",{R}}}")).IsError);
        Assert.True(s.Tool("update_system_prompt", A($"{{\"section\":\"dictation\",\"content\":\"  \",{R}}}")).IsError);
        Assert.True(s.Tool("update_system_prompt", A($"{{\"section\":\"dictation\",\"content\":\"{new string('x', 10_001)}\",{R}}}")).IsError);
    }

    [Fact]
    public void AcceptAfterManualChange_IsConflict_AndStaysPending()
    {
        using var s = new McpStack();
        var o = s.ToolJson("update_system_prompt", A($"{{\"section\":\"dictation\",\"content\":\"Neu\",{R}}}"));
        s.Settings.DictateSystemPrompt = "Von Hand";
        var ex = Assert.Throws<McpConfigException>(() => s.Proposals.Accept(o["id"]!.GetValue<string>()));
        Assert.Contains("inzwischen geändert", ex.Message);
        Assert.Single(s.Proposals.Pending);
        Assert.Equal("Von Hand", s.Settings.DictateSystemPrompt);
    }

    [Fact]
    public void NewerProposal_DeclinesTheOlderOne()
    {
        using var s = new McpStack();
        var a = s.ToolJson("update_system_prompt", A($"{{\"section\":\"dictation\",\"content\":\"Eins\",{R}}}"));
        var d = s.ToolJson("update_system_prompt", A($"{{\"section\":\"dictate_prompt\",\"content\":\"Zwei\",{R}}}"));
        var b = s.ToolJson("update_system_prompt", A($"{{\"section\":\"dictation\",\"content\":\"Drei\",{R}}}"));
        Assert.Equal(McpProposalStatus.Declined, s.Proposals.Find(a["id"]!.GetValue<string>())!.Status);
        Assert.Equal(McpProposalStatus.Pending, s.Proposals.Find(d["id"]!.GetValue<string>())!.Status);
        Assert.Equal(2, s.Proposals.Pending.Count);
        s.Proposals.Decline(b["id"]!.GetValue<string>());
        Assert.Single(s.Proposals.Pending);
    }

    [Fact]
    public void QuickInstructions_Apply_AndTooLongRejected()
    {
        using var s = new McpStack();
        var o = s.ToolJson("update_quick_instructions", A($"{{\"instructions\":[\"Als Arztbrief formulieren\",\"Kürzer\"],{R}}}"));
        Assert.Equal("applied", o["status"]!.GetValue<string>());
        Assert.Equal(new[] { "Als Arztbrief formulieren", "Kürzer" }, s.ReloadSettings().QuickActions);

        var same = s.ToolJson("update_quick_instructions", A($"{{\"instructions\":[\"Als Arztbrief formulieren\",\"Kürzer\"],{R}}}"));
        Assert.Equal("no_change", same["status"]!.GetValue<string>());

        Assert.True(s.Tool("update_quick_instructions", A($"{{\"instructions\":[\"{new string('x', 81)}\"],{R}}}")).IsError);
        Assert.True(s.Tool("update_quick_instructions", A($"{{\"instructions\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\"],{R}}}")).IsError);
        Assert.Equal(2, s.ReloadSettings().QuickActions.Count);
    }

    [Fact]
    public void GetConfiguration_HasRecipe_AndNeverTheKey()
    {
        using var s = new McpStack();
        s.Settings.ApiKey = "AIzaSECRET0123456789012345678901";
        s.Settings.OpenAiKey = "sk-SECRETSECRET";
        s.Settings.Glossary = new() { "Gödde" };
        var r = s.Tool("get_configuration", A("{\"include\":[\"system_prompts\",\"glossary\",\"template\"]}"), McpAccess.Read);
        Assert.False(r.IsError);
        Assert.DoesNotContain("AIzaSECRET", r.Text);
        Assert.DoesNotContain("sk-SECRET", r.Text);
        var o = JsonNode.Parse(r.Text)!;
        Assert.Equal(McpTuneRecipe.Text(null), o["tune_recipe"]!.GetValue<string>());
        Assert.Equal("windows", o["platform"]!.GetValue<string>());
        Assert.Equal("9.9.9", o["app_version"]!.GetValue<string>());
        Assert.True(o["api_keys_set"]!["gemini"]!.GetValue<bool>());
        Assert.Equal("Ctrl+Shift+D", o["hotkeys"]!["dictate"]!.GetValue<string>());
        Assert.Equal(1, o["glossary"]!["count"]!.GetValue<int>());
        Assert.False(o["system_prompts"]!["dictation"]!["customized"]!.GetValue<bool>());
        Assert.Equal(Prompts.DefaultTranscriptionSystemPrompt.Length, o["system_prompts"]!["dictation"]!["default_length"]!.GetValue<int>());
        Assert.Equal(Prompts.DefaultTranscriptionSystemPrompt, o["system_prompt_texts"]!["dictation"]!.GetValue<string>());
        Assert.Equal("Gödde", o["glossary_terms"]![0]!.GetValue<string>());
        Assert.Equal("apply", o["ai_app_access"]!["level"]!.GetValue<string>());
        var lean = JsonNode.Parse(s.Tool("get_configuration", new JsonObject(), McpAccess.Read).Text)!;
        Assert.Null(lean["system_prompt_texts"]);
    }

    [Fact]
    public void ReportProblem_WritesFile_Opens_AndRedactsKeys()
    {
        using var s = new McpStack();
        var key = "AIza" + new string('a', 30);
        var r = s.Tool("report_problem", A($"{{\"title\":\"Text wird doppelt eingefügt\",\"what_happens\":\"Es erscheint zweimal. Key {key}\",\"examples\":[\"Hallo Hallo\"],\"suspected_cause\":\"Paste-Timing\",\"why_not_settings\":\"Keine Einstellung steuert das.\"}}"));
        Assert.False(r.IsError, r.Text);
        Assert.Contains("Nothing is sent", r.Text);
        var path = Assert.Single(s.Host.OpenedReports);
        Assert.StartsWith(s.ReportDir, path);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain(key, text);
        Assert.Contains("AIza…", text);
        Assert.Contains("# Text wird doppelt eingefügt", text);
        Assert.Contains("## Was passiert", text);
        Assert.Contains("- Hallo Hallo", text);
        Assert.Contains("## Vermutete Ursache", text);
        Assert.Contains("## Warum keine Einstellung hilft", text);
        Assert.Contains("Es wurde nichts gesendet", text);
        Assert.Contains("Version: 9.9.9", text);
    }

    [Fact]
    public void ReportProblem_NeedsWriteAccess_AndValidInput()
    {
        using var s = new McpStack(McpAccessLevel.Read);
        Assert.True(s.Tool("report_problem", A("{\"title\":\"a\",\"what_happens\":\"b\",\"why_not_settings\":\"c\"}")).IsError);
        s.Config.AccessLevel = McpAccessLevel.Propose;
        Assert.True(s.Tool("report_problem", A("{\"title\":\"a\",\"what_happens\":\"b\"}")).IsError);
        Assert.True(s.Tool("report_problem", A($"{{\"title\":\"{new string('x', 121)}\",\"what_happens\":\"b\",\"why_not_settings\":\"c\"}}")).IsError);
        Assert.Empty(s.Host.OpenedReports);
    }
}
