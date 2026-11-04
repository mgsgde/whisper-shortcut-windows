using System.Text.Json.Nodes;
using WhisperShortcut.Core;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests.Mcp;

public class McpSettingsTests
{
    private static JsonObject A(string json) => McpStack.Args(json);
    private const string R = "\"rationale\":\"Passt besser.\"";

    [Fact]
    public void GetSettings_ListsCatalog_WithOptions()
    {
        using var s = new McpStack();
        var o = s.ToolJson("get_settings", new JsonObject(), McpAccess.Read);
        var list = o["settings"]!.AsArray();
        Assert.Equal(22, list.Count);
        var engine = list.First(x => x!["id"]!.GetValue<string>() == "dictation.engine")!;
        Assert.Equal("enum", engine["type"]!.GetValue<string>());
        Assert.Equal(3, engine["options"]!.AsArray().Count);
        Assert.False(engine["options"]![0]!["available"]!.GetValue<bool>());
        Assert.NotNull(engine["options"]![0]!["reason"]);
        var hot = list.First(x => x!["id"]!.GetValue<string>() == "shortcuts.dictate")!;
        Assert.Equal("shortcut", hot["type"]!.GetValue<string>());
        Assert.Equal("Ctrl+Shift+D", hot["value"]!.GetValue<string>());
        Assert.Equal(McpSettingsCatalog.ShortcutFormatHint, hot["format"]!.GetValue<string>());
        Assert.True(list.First(x => x!["id"]!.GetValue<string>() == "general.launch_at_login")!["requires_approval"]!.GetValue<bool>());

        var models = s.ToolJson("get_settings", A("{\"category\":\"models\"}"), McpAccess.Read)["settings"]!.AsArray();
        Assert.Equal(9, models.Count);
    }

    [Fact]
    public void Punctuation_AppliesBothWays()
    {
        using var s = new McpStack();
        Assert.Equal("applied", s.ToolJson("update_settings", A($"{{\"changes\":{{\"dictation.punctuation\":\"spokenOnly\"}},{R}}}"))["status"]!.GetValue<string>());
        Assert.Equal(PunctuationMode.SpokenOnly, s.ReloadSettings().PunctuationMode);
        Assert.Equal("applied", s.ToolJson("update_settings", A($"{{\"changes\":{{\"dictation.punctuation\":\"automatic\"}},{R}}}"))["status"]!.GetValue<string>());
        Assert.Equal(PunctuationMode.Automatic, s.ReloadSettings().PunctuationMode);
    }

    [Fact]
    public void UnavailableOptions_AreRejectedWithTheirReason()
    {
        using var s = new McpStack();
        var local = s.Tool("update_settings", A($"{{\"changes\":{{\"dictation.engine\":\"local\"}},{R}}}"));
        Assert.True(local.IsError);
        Assert.Contains("offline speech model is not downloaded", local.Text);
        var gem = s.Tool("update_settings", A($"{{\"changes\":{{\"dictation.engine\":\"openai\"}},{R}}}"));
        Assert.True(gem.IsError);
        Assert.Contains("No OpenAI API key", gem.Text);
        s.Settings.SpeechEngine = Engine.OpenAI;
        var back = s.Tool("update_settings", A($"{{\"changes\":{{\"dictation.engine\":\"gemini\"}},{R}}}"));
        Assert.True(back.IsError);
        Assert.Contains("No Gemini API key", back.Text);

        s.Settings.ApiKey = "AIzaKEY";
        s.Host.SpeechModelInstalled = true;
        Assert.Equal("applied", s.ToolJson("update_settings", A($"{{\"changes\":{{\"dictation.engine\":\"gemini\"}},{R}}}"))["status"]!.GetValue<string>());
        Assert.Equal("applied", s.ToolJson("update_settings", A($"{{\"changes\":{{\"dictation.engine\":\"local\"}},{R}}}"))["status"]!.GetValue<string>());
        Assert.Equal(Engine.Local, s.ReloadSettings().SpeechEngine);
    }

    [Theory]
    [InlineData("Ctrl+D")]
    [InlineData("Ctrl+Alt+Q")]
    [InlineData("Shift+Alt+F13")]
    [InlineData("hallo")]
    public void InvalidOrUnsafeHotkeys_AreRejected(string hotkey)
    {
        using var s = new McpStack();
        var r = s.Tool("update_settings", A($"{{\"changes\":{{\"shortcuts.dictate\":\"{hotkey}\"}},{R}}}"));
        Assert.True(r.IsError);
        Assert.Equal("Ctrl+Shift+D", s.Settings.DictateHotkey);
    }

    [Fact]
    public void DuplicateHotkeys_InOneBatch_AreRejected_AlsoAgainstUnchangedOnes()
    {
        using var s = new McpStack();
        var both = s.Tool("update_settings", A($"{{\"changes\":{{\"shortcuts.dictate\":\"Ctrl+Shift+X\",\"shortcuts.dictate_prompt\":\"strg+umschalt+x\"}},{R}}}"));
        Assert.True(both.IsError);
        Assert.Contains("must be different", both.Text);
        var against = s.Tool("update_settings", A($"{{\"changes\":{{\"shortcuts.dictate\":\"Ctrl+Shift+P\"}},{R}}}"));
        Assert.True(against.IsError);
        Assert.Equal("Ctrl+Shift+D", s.Settings.DictateHotkey);
        // Swapping two is fine: the state the batch leaves is distinct.
        var swap = s.ToolJson("update_settings", A($"{{\"changes\":{{\"shortcuts.dictate\":\"Ctrl+Shift+P\",\"shortcuts.dictate_prompt\":\"Ctrl+Shift+D\"}},{R}}}"));
        Assert.Equal("applied", swap["status"]!.GetValue<string>());
    }

    [Fact]
    public void HotkeyObjectForm_IsAcceptedAndNormalized()
    {
        using var s = new McpStack();
        var o = s.ToolJson("update_settings", A($"{{\"changes\":{{\"shortcuts.dictate\":{{\"key\":\"x\",\"modifiers\":[\"control\",\"shift\"]}}}},{R}}}"));
        Assert.Equal("applied", o["status"]!.GetValue<string>());
        Assert.Equal("Ctrl+Shift+X", s.ReloadSettings().DictateHotkey);
        Assert.Equal("Strg+Umschalt+D", o["changed"]![0]!["from"]!.GetValue<string>());
        Assert.Equal("Strg+Umschalt+X", o["changed"]![0]!["to"]!.GetValue<string>());
        Assert.Equal(1, s.Host.AfterApplyCount);
    }

    [Fact]
    public void Apply_ChangesSettings_AndSaves()
    {
        using var s = new McpStack();
        var o = s.ToolJson("update_settings", A($"{{\"changes\":{{\"general.sounds\":false,\"dictate_prompt.model\":\"gemini-3.5-flash-lite\"}},{R}}}"));
        Assert.Equal("applied", o["status"]!.GetValue<string>());
        var disk = s.ReloadSettings();
        Assert.False(disk.Sounds);
        Assert.Equal("gemini-3.5-flash-lite", disk.PromptModel);
        Assert.Equal("2 Einstellungen geändert", s.Changes.Changes[0].Title);
        Assert.False(s.Tool("update_settings", A($"{{\"changes\":{{\"general.sounds\":true}},{R}}}")).IsError);
        Assert.Equal("Einstellung: Signaltöne Aus → An", s.Changes.Changes[0].Title);
    }

    [Fact]
    public void NoOp_IsNoChange_AndBadInputIsRejected()
    {
        using var s = new McpStack();
        var o = s.ToolJson("update_settings", A($"{{\"changes\":{{\"general.sounds\":true,\"shortcuts.dictate\":\"strg+umschalt+d\"}},{R}}}"));
        Assert.Equal("no_change", o["status"]!.GetValue<string>());
        Assert.Empty(o["changed"]!.AsArray());
        Assert.Equal(0, s.Host.AfterApplyCount);
        Assert.Contains("Unknown setting id: nope", s.Tool("update_settings", A($"{{\"changes\":{{\"nope\":1}},{R}}}")).Text);
        Assert.True(s.Tool("update_settings", A($"{{\"changes\":{{\"general.sounds\":\"yes\"}},{R}}}")).IsError);
        Assert.True(s.Tool("update_settings", A($"{{\"changes\":{{}},{R}}}")).IsError);
        Assert.True(s.Tool("update_settings", A("{\"changes\":{\"general.sounds\":false}}")).IsError);
    }

    [Fact]
    public void ProposeLevel_MakesOneProposal_ForSettings()
    {
        using var s = new McpStack(McpAccessLevel.Propose);
        var o = s.ToolJson("update_settings", A($"{{\"changes\":{{\"general.sounds\":false}},{R}}}"));
        Assert.Equal("pending_review", o["status"]!.GetValue<string>());
        Assert.True(s.Settings.Sounds);
        Assert.Equal("Einstellungen ändern", s.Host.Proposals[0].Title);
    }

    [Fact]
    public void LaunchAtLogin_IsAProposal_EvenAtApply_AndAcceptApplies()
    {
        using var s = new McpStack(McpAccessLevel.Apply);
        var o = s.ToolJson("update_settings", A($"{{\"changes\":{{\"general.launch_at_login\":true,\"general.sounds\":false}},{R}}}"));
        Assert.Equal("pending_review", o["status"]!.GetValue<string>());
        Assert.Empty(s.Host.AutoStartCalls);
        Assert.True(s.Settings.Sounds);

        s.Proposals.Accept(o["id"]!.GetValue<string>());
        Assert.Equal(new[] { true }, s.Host.AutoStartCalls);
        Assert.False(s.ReloadSettings().Sounds);
        Assert.Equal(1, s.Host.AfterApplyCount);
    }

    [Fact]
    public void AcceptedSettingsProposal_IsRevalidatedAgainstToday()
    {
        using var s = new McpStack(McpAccessLevel.Propose);
        s.Settings.ApiKey = "AIzaKEY";
        s.Settings.SpeechEngine = Engine.OpenAI;
        s.Settings.OpenAiKey = "sk-1";
        var o = s.ToolJson("update_settings", A($"{{\"changes\":{{\"dictation.engine\":\"gemini\"}},{R}}}"));
        s.Settings.ApiKey = ""; // the user removed the key meanwhile
        var ex = Assert.Throws<McpConfigException>(() => s.Proposals.Accept(o["id"]!.GetValue<string>()));
        Assert.Contains("nicht mehr möglich", ex.Message);
        Assert.Equal(Engine.OpenAI, s.Settings.SpeechEngine);
        Assert.Single(s.Proposals.Pending);
    }

    [Fact]
    public void LaunchAtLogin_FailureIsReported_AndNothingIsSaved()
    {
        using var s = new McpStack(McpAccessLevel.Propose);
        s.Host.AutoStartResult = false;
        var o = s.ToolJson("update_settings", A($"{{\"changes\":{{\"general.launch_at_login\":true}},{R}}}"));
        var ex = Assert.Throws<McpConfigException>(() => s.Proposals.Accept(o["id"]!.GetValue<string>()));
        Assert.Equal("Der automatische Start konnte nicht geändert werden.", ex.Message);
        Assert.Empty(s.Changes.Changes);
    }
    [Fact]
    public void ReadAloud_SettingsAreListed()
    {
        using var s = new McpStack();
        var ra = s.ToolJson("get_settings", A("{\"category\":\"read_aloud\"}"), McpAccess.Read)["settings"]!.AsArray();
        Assert.Equal(new[] { "read_aloud.smart_rewrite", "read_aloud.speed", "read_aloud.voice.gemini", "read_aloud.voice.openai" },
            ra.Select(x => x!["id"]!.GetValue<string>()).OrderBy(x => x).ToArray());
        var speed = ra.First(x => x!["id"]!.GetValue<string>() == "read_aloud.speed")!;
        Assert.Equal("1.0", speed["value"]!.GetValue<string>());
        Assert.Equal(new[] { "0.75", "1.0", "1.25", "1.5", "1.75", "2.0" },
            speed["options"]!.AsArray().Select(o => o!["value"]!.GetValue<string>()).ToArray());
        Assert.Equal("1,5\u00D7", speed["options"]![3]!["label"]!.GetValue<string>());

        var all = s.ToolJson("get_settings", new JsonObject(), McpAccess.Read)["settings"]!.AsArray();
        var hot = all.First(x => x!["id"]!.GetValue<string>() == "shortcuts.read_aloud")!;
        Assert.Equal("shortcut", hot["type"]!.GetValue<string>());
        var model = all.First(x => x!["id"]!.GetValue<string>() == "read_aloud.model")!;
        Assert.Equal("models", model["category"]!.GetValue<string>());
        Assert.Equal(4, model["options"]!.AsArray().Count); // Gemini Flash-Lite, Gemini Flash, GPT-4o mini, Windows
    }

    [Fact]
    public void ReadAloud_SpeedWrites_AndUnknownVoiceIsRefused()
    {
        using var s = new McpStack();
        Assert.Equal("applied", s.ToolJson("update_settings", A($"{{\"changes\":{{\"read_aloud.speed\":\"1.5\"}},{R}}}"))["status"]!.GetValue<string>());
        Assert.Equal(1.5, s.ReloadSettings().ReadAloudSpeed);

        Assert.True(s.Tool("update_settings", A($"{{\"changes\":{{\"read_aloud.speed\":\"3\"}},{R}}}")).IsError);
        Assert.Equal(1.5, s.ReloadSettings().ReadAloudSpeed);

        Assert.Equal("applied", s.ToolJson("update_settings", A($"{{\"changes\":{{\"read_aloud.voice.gemini\":\"Kore\"}},{R}}}"))["status"]!.GetValue<string>());
        Assert.Equal("Kore", s.ReloadSettings().ReadAloudVoiceGemini);
        var bad = s.Tool("update_settings", A($"{{\"changes\":{{\"read_aloud.voice.gemini\":\"Nobody\"}},{R}}}"));
        Assert.True(bad.IsError);
        Assert.Equal("Kore", s.ReloadSettings().ReadAloudVoiceGemini);

        Assert.Equal("applied", s.ToolJson("update_settings", A($"{{\"changes\":{{\"read_aloud.smart_rewrite\":false}},{R}}}"))["status"]!.GetValue<string>());
        Assert.False(s.ReloadSettings().ReadAloudSmartRewrite);
    }

    [Fact]
    public void ReadAloud_OpenAiModelNeedsAnOpenAiKey()
    {
        using var s = new McpStack();
        var refused = s.Tool("update_settings", A($"{{\"changes\":{{\"read_aloud.model\":\"gpt-4o-mini-tts\"}},{R}}}"));
        Assert.True(refused.IsError);
        Assert.Contains("No OpenAI API key", refused.Text);

        Assert.Equal("applied", s.ToolJson("update_settings", A($"{{\"changes\":{{\"read_aloud.model\":\"gemini-3.8-flash-tts\"}},{R}}}"))["status"]!.GetValue<string>());
        Assert.Equal("gemini-3.8-flash-tts", s.ReloadSettings().ReadAloudModel);

        s.Settings.OpenAiKey = "sk-test";
        Assert.Equal("applied", s.ToolJson("update_settings", A($"{{\"changes\":{{\"read_aloud.model\":\"gpt-4o-mini-tts\"}},{R}}}"))["status"]!.GetValue<string>());
        Assert.Equal("gpt-4o-mini-tts", s.ReloadSettings().ReadAloudModel);
    }

    [Fact]
    public void ReadAloudHotkey_MustDifferFromTheOthers()
    {
        using var s = new McpStack();
        Assert.Equal("applied", s.ToolJson("update_settings", A($"{{\"changes\":{{\"shortcuts.read_aloud\":\"Alt+4\"}},{R}}}"))["status"]!.GetValue<string>());
        Assert.Equal("Alt+4", s.ReloadSettings().ReadAloudHotkey);
        var clash = s.Tool("update_settings", A($"{{\"changes\":{{\"shortcuts.dictate\":\"Alt+4\"}},{R}}}"));
        Assert.True(clash.IsError);
        Assert.Contains("Tastenkürzel Vorlesen", clash.Text);
    }
}
