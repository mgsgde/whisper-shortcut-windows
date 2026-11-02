using System.Text.Json.Nodes;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests.Mcp;

public class McpDispatcherTests
{
    private sealed class EchoProvider : IMcpToolProvider
    {
        public IEnumerable<JsonObject> ToolDefinitions(McpAccess access) =>
            new[] { new JsonObject { ["name"] = "fake_echo" } };
        public bool Handles(string toolName) => toolName == "fake_echo";
        public McpToolResult Call(string toolName, JsonObject arguments, McpAccess access) =>
            new("echo:" + arguments["x"], false);
    }

    private sealed class ThrowingProvider : IMcpToolProvider
    {
        public IEnumerable<JsonObject> ToolDefinitions(McpAccess access) => Array.Empty<JsonObject>();
        public bool Handles(string toolName) => toolName == "boom";
        public McpToolResult Call(string toolName, JsonObject arguments, McpAccess access) => throw new InvalidOperationException("kaputt");
    }

    private static JsonObject Msg(string method, string? paramsJson = null, int? id = 1)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id != null) o["id"] = id;
        if (paramsJson != null) o["params"] = JsonNode.Parse(paramsJson);
        return o;
    }

    [Fact]
    public void Initialize_NegotiatesVersion_AndReportsClient()
    {
        using var s = new McpStack();
        (string, string, McpAccess)? seen = null;
        s.Dispatcher.OnInitialize = (n, v, a) => seen = (n, v, a);
        var r = s.Dispatcher.Dispatch(Msg("initialize", "{\"protocolVersion\":\"2025-03-26\",\"clientInfo\":{\"name\":\"Claude\",\"version\":\"1.2\"}}"), McpAccess.Write)!;
        Assert.Equal("2025-03-26", r["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("whisper-shortcut", r["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("9.9.9", r["result"]!["serverInfo"]!["version"]!.GetValue<string>());
        Assert.Equal(McpDispatcher.Instructions, r["result"]!["instructions"]!.GetValue<string>());
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);
        Assert.Equal(("Claude", "1.2", McpAccess.Write), seen);
    }

    [Fact]
    public void Initialize_UnknownVersion_FallsBackToNewest()
    {
        using var s = new McpStack();
        var r = s.Dispatcher.Dispatch(Msg("initialize", "{\"protocolVersion\":\"1999-01-01\"}"), McpAccess.Read)!;
        Assert.Equal("2025-06-18", r["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Notification_ReturnsNull()
    {
        using var s = new McpStack();
        Assert.Null(s.Dispatcher.Dispatch(Msg("notifications/initialized", id: null), McpAccess.Read));
        Assert.Null(s.Dispatcher.Dispatch(Msg("notifications/cancelled"), McpAccess.Read));
    }

    [Fact]
    public void UnknownMethod_And_InvalidRequest_AreJsonRpcErrors()
    {
        using var s = new McpStack();
        var r = s.Dispatcher.Dispatch(Msg("nope"), McpAccess.Read)!;
        Assert.Equal(-32601, r["error"]!["code"]!.GetValue<int>());
        Assert.Contains("nope", r["error"]!["message"]!.GetValue<string>());

        var bad = s.Dispatcher.Dispatch(new JsonObject { ["id"] = 7 }, McpAccess.Read)!;
        Assert.Equal(-32600, bad["error"]!["code"]!.GetValue<int>());
        Assert.Equal(7, bad["id"]!.GetValue<int>());
        var notObject = s.Dispatcher.Dispatch(new JsonArray(), McpAccess.Read)!;
        Assert.Equal(-32600, notObject["error"]!["code"]!.GetValue<int>());
        Assert.Null(notObject["id"]);
    }

    [Fact]
    public void ToolsList_HidesWriteTools_ForReadAccess()
    {
        using var s = new McpStack();
        string[] Names(McpAccess a) => s.Dispatcher.Dispatch(Msg("tools/list"), a)!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).ToArray();
        var read = Names(McpAccess.Read);
        Assert.Contains("get_configuration", read);
        Assert.Contains("get_settings", read);
        Assert.DoesNotContain("add_glossary_terms", read);
        Assert.DoesNotContain("update_settings", read);
        var write = Names(McpAccess.Write);
        foreach (var n in new[] { "add_glossary_terms", "update_system_prompt", "update_quick_instructions", "report_problem", "get_proposal_status", "update_settings" })
            Assert.Contains(n, write);
    }

    [Fact]
    public void ToolsCall_UnknownTool_IsErrorResult_AndMissingNameIsRpcError()
    {
        using var s = new McpStack();
        var r = s.Tool("does_not_exist", new JsonObject());
        Assert.True(r.IsError);
        Assert.Equal("Unknown tool: does_not_exist", r.Text);
        var missing = s.Dispatcher.Dispatch(Msg("tools/call", "{}"), McpAccess.Write)!;
        Assert.Equal(-32602, missing["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_RunOnMainNull_IsBusyMessage()
    {
        using var s = new McpStack();
        s.Dispatcher.RunOnMain = _ => null;
        var r = s.Tool("get_configuration", new JsonObject());
        Assert.True(r.IsError);
        Assert.Equal(McpDispatcher.BusyMessage, r.Text);
        var res = s.Dispatcher.Dispatch(Msg("resources/list"), McpAccess.Read)!;
        Assert.Equal(-32603, res["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_ProviderException_BecomesErrorResult()
    {
        var d = new McpDispatcher(new IMcpToolProvider[] { new ThrowingProvider() }, "1");
        var r = d.Dispatch(Msg("tools/call", "{\"name\":\"boom\"}"), McpAccess.Write)!;
        Assert.True(r["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("kaputt", r["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Prompts_GetReturnsRecipeWithFocusLine()
    {
        using var s = new McpStack();
        var list = s.Dispatcher.Dispatch(Msg("prompts/list"), McpAccess.Read)!;
        Assert.Equal(McpTuneRecipe.PromptName, list["result"]!["prompts"]![0]!["name"]!.GetValue<string>());
        var get = s.Dispatcher.Dispatch(Msg("prompts/get", "{\"name\":\"tune_whisper_shortcut\",\"arguments\":{\"focus\":\"names\"}}"), McpAccess.Read)!;
        var text = get["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>();
        Assert.StartsWith("Tune WhisperShortcut to the user.", text);
        Assert.EndsWith("Focus: names and terms. Fill the glossary with people, practices, companies, medications and domain terms the user dictates.", text);
        var unknown = s.Dispatcher.Dispatch(Msg("prompts/get", "{\"name\":\"other\"}"), McpAccess.Read)!;
        Assert.Equal(-32602, unknown["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Resources_ListAndRead_AndUnknownIsNotFound()
    {
        using var s = new McpStack();
        s.Settings.Glossary = new() { "Gödde" };
        var list = s.Dispatcher.Dispatch(Msg("resources/list"), McpAccess.Read)!["result"]!["resources"]!.AsArray();
        Assert.Equal(4, list.Count);
        var read = s.Dispatcher.Dispatch(Msg("resources/read", "{\"uri\":\"whisper-shortcut://glossary\"}"), McpAccess.Read)!;
        Assert.Equal("Gödde", read["result"]!["contents"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("text/markdown", read["result"]!["contents"]![0]!["mimeType"]!.GetValue<string>());
        var missing = s.Dispatcher.Dispatch(Msg("resources/read", "{\"uri\":\"whisper-shortcut://nope\"}"), McpAccess.Read)!;
        Assert.Equal(-32002, missing["error"]!["code"]!.GetValue<int>());

        var bare = new McpDispatcher(Array.Empty<IMcpToolProvider>(), "1");
        Assert.Empty(bare.Dispatch(Msg("resources/list"), McpAccess.Read)!["result"]!["resources"]!.AsArray());
    }

    [Fact]
    public void SecondProvider_IsListedAndCallable_WithoutTouchingTheFirst()
    {
        using var s = new McpStack();
        var d = new McpDispatcher(new IMcpToolProvider[] { s.ConfigTools, s.SettingsTools, new EchoProvider() }, "1", s.ConfigTools);
        var names = d.Dispatch(Msg("tools/list"), McpAccess.Read)!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal("fake_echo", names[^1]);
        var call = d.Dispatch(Msg("tools/call", "{\"name\":\"fake_echo\",\"arguments\":{\"x\":\"hi\"}}"), McpAccess.Read)!;
        Assert.Equal("echo:hi", call["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.False(call["result"]!["isError"]!.GetValue<bool>());
    }
}
