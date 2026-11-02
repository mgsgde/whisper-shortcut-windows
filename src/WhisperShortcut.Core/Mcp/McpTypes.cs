using System.Text.Json.Nodes;

namespace WhisperShortcut.Core.Mcp;

/// <summary>What the bearer token of a request grants: the read token gives <see cref="Read"/>, the write token <see cref="Write"/> (which includes read).</summary>
public enum McpAccess
{
    /// <summary>Read-only tools.</summary>
    Read,
    /// <summary>Read tools plus the write-set tools (each still gated by <see cref="McpAccessLevel"/>).</summary>
    Write,
}

/// <summary>What the user allows AI apps to do, chosen in Settings. JSON values "read" / "propose" / "apply".</summary>
public enum McpAccessLevel
{
    /// <summary>Nothing may be changed, even with the write token.</summary>
    Read,
    /// <summary>Changes become proposals the user accepts in the app.</summary>
    Propose,
    /// <summary>Changes are applied at once (instruction changes still wait for approval).</summary>
    Apply,
}

/// <summary>Result of one tool call as the AI app sees it: plain text (usually JSON) plus the error flag.</summary>
/// <param name="Text">What the AI app reads.</param>
/// <param name="IsError">True when the call failed or was refused.</param>
public sealed record McpToolResult(string Text, bool IsError);

/// <summary>A failure whose message is meant for the user or the AI app (not a bug); never logged with content.</summary>
public sealed class McpConfigException(string message) : Exception(message);

/// <summary>A group of MCP tools. Registered with <see cref="McpDispatcher"/>; adding a provider never touches the others.</summary>
public interface IMcpToolProvider
{
    /// <summary>Definitions visible to a client with this token; fresh objects on every call (callers may attach them elsewhere).</summary>
    IEnumerable<JsonObject> ToolDefinitions(McpAccess access);

    /// <summary>True when this provider owns <paramref name="toolName"/>.</summary>
    bool Handles(string toolName);

    /// <summary>Runs the tool. Failures the AI app should see are returned as an error result, not thrown.</summary>
    McpToolResult Call(string toolName, JsonObject arguments, McpAccess access);
}

/// <summary>Read-only documents an AI app can fetch as MCP resources.</summary>
public interface IMcpResourceProvider
{
    /// <summary>Resource descriptors: {uri, name, description, mimeType}.</summary>
    IEnumerable<JsonObject> Resources();

    /// <summary>The Markdown text of <paramref name="uri"/>, or null when unknown.</summary>
    string? ReadResource(string uri);
}

/// <summary>What the tools need from the running app (implemented in the WinForms project; tests use a fake).</summary>
public interface IMcpHost
{
    /// <summary>The app version shown to AI apps and in problem reports.</summary>
    string AppVersion { get; }

    /// <summary>True when the Parakeet speech model is downloaded.</summary>
    bool SpeechModelInstalled { get; }

    /// <summary>True when that local LLM and llama-server are downloaded.</summary>
    bool LocalLlmInstalled(string llmId);

    /// <summary>Whether the app starts with Windows; null = unknown.</summary>
    bool? AutoStartEnabled { get; }

    /// <summary>Turns start-with-Windows on or off; false = failed.</summary>
    bool SetAutoStart(bool enabled);

    /// <summary>Non-null: an English sentence saying why writes must wait (recording running, Settings window has unsaved edits).</summary>
    string? WriteBlockedReason();

    /// <summary>Settings were changed and saved: re-register hotkeys etc.</summary>
    void AfterApply();

    /// <summary>A proposal was created: show the approval dialog.</summary>
    void ProposalCreated(McpProposal proposal);

    /// <summary>Opens the saved problem report file for the user.</summary>
    void OpenProblemReport(string path);
}
