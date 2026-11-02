using System.Text;
using System.Text.Json.Nodes;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests.Mcp;

public class McpHttpHandlerTests
{
    private const string Read = "readtoken";
    private const string Write = "writetoken";

    private static McpHttpRequest Req(string body, string method = "POST", string path = "/mcp", string? token = Write,
        string? origin = null, string? host = "127.0.0.1:47823")
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token != null) h["Authorization"] = "Bearer " + token;
        if (origin != null) h["Origin"] = origin;
        if (host != null) h["Host"] = host;
        return new McpHttpRequest(method, path, h, Encoding.UTF8.GetBytes(body));
    }

    private static string ListBody => "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";

    private static McpHttpResponse Handle(McpStack s, McpHttpRequest r) => McpHttpHandler.Handle(r, Read, Write, s.Dispatcher);

    private static JsonNode Body(McpHttpResponse r) => JsonNode.Parse(r.Body)!;

    [Fact]
    public void WrongPath_Is404_QueryIsIgnored()
    {
        using var s = new McpStack();
        Assert.Equal(404, Handle(s, Req(ListBody, path: "/other")).Status);
        Assert.Equal(200, Handle(s, Req(ListBody, path: "/mcp?x=1")).Status);
    }

    [Fact]
    public void MissingOrWrongToken_Is401_WithChallenge()
    {
        using var s = new McpStack();
        foreach (var token in new string?[] { null, "wrong", "" })
        {
            var r = Handle(s, Req(ListBody, token: token));
            Assert.Equal(401, r.Status);
            Assert.Equal("Bearer", r.Headers["WWW-Authenticate"]);
            Assert.Equal("unauthorized", Body(r)["error"]!.GetValue<string>());
        }
    }

    [Fact]
    public void EmptyTokens_NeverMatch()
    {
        using var s = new McpStack();
        var r = McpHttpHandler.Handle(Req(ListBody, token: ""), "", "", s.Dispatcher);
        Assert.Equal(401, r.Status);
    }

    [Fact]
    public void ForeignOrigin_AndForeignHost_Are403()
    {
        using var s = new McpStack();
        Assert.Equal(403, Handle(s, Req(ListBody, origin: "https://evil.example")).Status);
        Assert.Equal(403, Handle(s, Req(ListBody, origin: "null")).Status);
        Assert.Equal(200, Handle(s, Req(ListBody, origin: "http://localhost:3000")).Status);
        Assert.Equal(200, Handle(s, Req(ListBody, origin: "http://[::1]:3000")).Status);
        Assert.Equal(403, Handle(s, Req(ListBody, host: "evil.example:47823")).Status);
        Assert.Equal(200, Handle(s, Req(ListBody, host: "localhost:47823")).Status);
        Assert.Equal(200, Handle(s, Req(ListBody, host: "[::1]:47823")).Status);
    }

    [Fact]
    public void Get_Is405_WithAllow()
    {
        using var s = new McpStack();
        var r = Handle(s, Req("", method: "GET"));
        Assert.Equal(405, r.Status);
        Assert.Equal("POST", r.Headers["Allow"]);
    }

    [Fact]
    public void BadJson_IsParseError()
    {
        using var s = new McpStack();
        var r = Handle(s, Req("{nope"));
        Assert.Equal(200, r.Status);
        Assert.Equal(-32700, Body(r)["error"]!["code"]!.GetValue<int>());
        Assert.Null(Body(r)["id"]);
    }

    [Fact]
    public void Batch_ReturnsArray_AndAllNotificationsGive202()
    {
        using var s = new McpStack();
        var r = Handle(s, Req("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}]"));
        Assert.Equal(200, r.Status);
        Assert.Equal("application/json", r.Headers["Content-Type"]);
        Assert.Equal(2, Body(r).AsArray().Count);
        var n = Handle(s, Req("[{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}]"));
        Assert.Equal(202, n.Status);
        Assert.Empty(n.Body);
        Assert.Equal(202, Handle(s, Req("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}")).Status);
    }

    [Fact]
    public void ReadToken_MeansReadAccess_WriteToken_MeansWrite()
    {
        using var s = new McpStack();
        static List<string> Names(McpHttpResponse r) => Body(r)["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("add_glossary_terms", Names(Handle(s, Req(ListBody, token: Read))));
        Assert.Contains("add_glossary_terms", Names(Handle(s, Req(ListBody, token: Write))));
    }
}
