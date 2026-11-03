using System.Text.Json.Nodes;
using WhisperShortcut.Core;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests.Mcp;

/// <summary>IMcpHost that records what the tools ask of the app.</summary>
public sealed class FakeHost : IMcpHost
{
    public string AppVersion { get; set; } = "9.9.9";
    public bool SpeechModelInstalled { get; set; }
    public bool LlmInstalled { get; set; }
    public bool? AutoStartEnabled { get; set; } = false;
    public bool AutoStartResult { get; set; } = true;
    public string? BlockedReason { get; set; }
    public int AfterApplyCount { get; private set; }
    public List<McpProposal> Proposals { get; } = new();
    public List<string> OpenedReports { get; } = new();
    public List<bool> AutoStartCalls { get; } = new();

    public bool LocalLlmInstalled(string llmId) => LlmInstalled;
    public bool SetAutoStart(bool enabled)
    {
        AutoStartCalls.Add(enabled);
        if (AutoStartResult) AutoStartEnabled = enabled;
        return AutoStartResult;
    }
    public string? WriteBlockedReason() => BlockedReason;
    public void AfterApply() => AfterApplyCount++;
    public void ProposalCreated(McpProposal proposal) => Proposals.Add(proposal);
    public void OpenProblemReport(string path) => OpenedReports.Add(path);
}

/// <summary>Reverses the secret so tests can tell protected from plain text.</summary>
public sealed class ReverseProtector : ISecretProtector
{
    public string Protect(string plain) => new(plain.Reverse().ToArray());
    public string? Unprotect(string stored) => new(stored.Reverse().ToArray());
}

/// <summary>The whole MCP stack over a temp folder.</summary>
public sealed class McpStack : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "wsw-mcp-" + Guid.NewGuid().ToString("N"));
    public string SettingsPath => Path.Combine(Dir, "settings.json");
    public string HistoryPath => Path.Combine(Dir, "prompt-history.json");
    public string ChangesPath => Path.Combine(Dir, "mcp-changes.jsonl");
    public string ProposalsPath => Path.Combine(Dir, "mcp-proposals.jsonl");
    public string ReportDir => Path.Combine(Dir, "reports");
    public FakeHost Host { get; } = new();
    public Settings Settings { get; }
    public McpConfig Config { get; }
    public McpChangeLog Changes { get; }
    public McpProposalStore Proposals { get; }
    public IReadOnlyList<McpSetting> Catalog { get; }
    public McpConfigApplier Applier { get; }
    public McpToolContext Ctx { get; }
    public McpConfigTools ConfigTools { get; }
    public McpSettingsTools SettingsTools { get; }
    public McpDispatcher Dispatcher { get; }

    public McpStack(McpAccessLevel level = McpAccessLevel.Apply)
    {
        Directory.CreateDirectory(Dir);
        // An install from before the Alt+digit defaults: pinned to Ctrl+Shift+D/P/K, the values these tests were written for.
        File.WriteAllText(SettingsPath, "{}");
        Settings = Settings.Load(SettingsPath, new PlainSecretProtector());
        Config = McpConfig.Load(Path.Combine(Dir, "mcp.json"), new PlainSecretProtector());
        Config.AccessLevel = level;
        Changes = new McpChangeLog(ChangesPath);
        Proposals = new McpProposalStore(ProposalsPath);
        Catalog = McpSettingsCatalog.Build(Settings, Host);
        Applier = new McpConfigApplier(Settings, Changes, Catalog, Host, () => PromptHistory.Load(HistoryPath));
        Proposals.Applier = Applier;
        Ctx = new McpToolContext(Settings, Host, Config, Proposals, Changes, Applier, ReportDir);
        ConfigTools = new McpConfigTools(Ctx);
        SettingsTools = new McpSettingsTools(Ctx);
        Dispatcher = new McpDispatcher(new IMcpToolProvider[] { ConfigTools, SettingsTools }, "9.9.9", ConfigTools);
    }

    public Settings ReloadSettings() => Settings.Load(SettingsPath, new PlainSecretProtector());

    /// <summary>Calls a tool through the dispatcher; returns the result text and flag.</summary>
    public McpToolResult Tool(string name, JsonObject args, McpAccess access = McpAccess.Write)
    {
        var resp = Dispatcher.Dispatch(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args },
        }, access)!;
        var result = resp["result"]!;
        return new McpToolResult(result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    public JsonObject ToolJson(string name, JsonObject args, McpAccess access = McpAccess.Write)
    {
        var r = Tool(name, args, access);
        Assert.False(r.IsError, r.Text);
        return JsonNode.Parse(r.Text)!.AsObject();
    }

    public static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { }
    }
}
