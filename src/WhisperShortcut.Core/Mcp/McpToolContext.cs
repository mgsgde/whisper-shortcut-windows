using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppLog = WhisperShortcut.Core.Log;

namespace WhisperShortcut.Core.Mcp;

/// <summary>Everything the tool providers share: the live state, the stores, and the common checks and result shapes.</summary>
public sealed class McpToolContext
{
    /// <summary>Longest accepted rationale.</summary>
    public const int MaxRationaleCharacters = 300;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Creates the context; <paramref name="reportDirectory"/> defaults to Problemberichte in the settings folder.</summary>
    public McpToolContext(Settings settings, IMcpHost host, McpConfig config, McpProposalStore proposals, McpChangeLog log, McpConfigApplier applier, string? reportDirectory = null)
    {
        Settings = settings;
        Host = host;
        Config = config;
        Proposals = proposals;
        Log = log;
        Applier = applier;
        ReportDirectory = reportDirectory ?? Path.Combine(AppData.SettingsFolder, "Problemberichte");
    }

    /// <summary>The live settings.</summary>
    public Settings Settings { get; }
    /// <summary>The running app.</summary>
    public IMcpHost Host { get; }
    /// <summary>Server switch, access level, tokens.</summary>
    public McpConfig Config { get; }
    /// <summary>Pending and resolved proposals.</summary>
    public McpProposalStore Proposals { get; }
    /// <summary>The change list.</summary>
    public McpChangeLog Log { get; }
    /// <summary>Writes settings and undoes changes.</summary>
    public McpConfigApplier Applier { get; }
    /// <summary>Where problem reports are saved.</summary>
    public string ReportDirectory { get; }

    /// <summary>Null = the call may write. Otherwise the error result to return.</summary>
    public McpToolResult? CheckWrite(string tool, McpAccess access)
    {
        if (access != McpAccess.Write)
            return Reject(tool, "read token", "This connection can only read. Connect again from WhisperShortcut Settings → Claude & KI-Apps to get the key that may change settings.");
        if (Config.AccessLevel == McpAccessLevel.Read)
            return Reject(tool, "access level read", "Changing settings from AI apps is off in WhisperShortcut Settings → Claude & KI-Apps.");
        var blocked = Host.WriteBlockedReason();
        return blocked == null ? null : Reject(tool, "blocked", blocked);
    }

    /// <summary>Indented JSON with readable umlauts.</summary>
    public static string Json(JsonNode node) => node.ToJsonString(Pretty);

    /// <summary>The required one-line rationale, trimmed and at most <see cref="MaxRationaleCharacters"/> long.</summary>
    public (string? Value, McpToolResult? Error) Rationale(JsonObject args, string tool)
    {
        var r = args["rationale"] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        if (r.Length == 0)
            return (null, Reject(tool, "rationale missing", "rationale is required: one line on why this change helps the user."));
        if (r.Length > MaxRationaleCharacters)
            return (null, Reject(tool, "rationale too long", $"rationale has {r.Length} characters; the limit is {MaxRationaleCharacters}."));
        return (r, null);
    }

    /// <summary>Logs the refusal (tool and reason only) and returns it as an error result.</summary>
    public McpToolResult Reject(string tool, string reason, string message)
    {
        AppLog.Info($"MCP: {tool} rejected ({reason})");
        return new McpToolResult(message, true);
    }

    /// <summary>The result of a call that became a proposal; shows the approval dialog through the host.</summary>
    public McpToolResult Pending(McpProposal p)
    {
        try { Host.ProposalCreated(p); }
        catch (Exception ex) { AppLog.Error("MCP: showing the proposal failed", ex); }
        return new McpToolResult(Json(new JsonObject
        {
            ["status"] = "pending_review",
            ["id"] = p.Id,
            ["message"] = "Waiting for the user to accept it in WhisperShortcut.",
        }), false);
    }
}
