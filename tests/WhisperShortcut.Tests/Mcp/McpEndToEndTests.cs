using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests.Mcp;

public class McpEndToEndTests
{
    private static LoopbackHttpServer Start(McpStack s, string read, string write) =>
        new(0, req => McpHttpHandler.Handle(req, read, write, s.Dispatcher));

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> Post(HttpClient c, int port, string body, string? token)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (token != null) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await c.SendAsync(msg);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static string Rpc(int id, string method, string? p = null) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\"{(p == null ? "" : ",\"params\":" + p)}}}";

    private static async Task<string> RawExchange(int port, string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task FullFlow_OverRealHttp()
    {
        using var s = new McpStack(McpAccessLevel.Apply);
        const string read = "r-token", write = "w-token";
        using var server = Start(s, read, write);
        using var http = new HttpClient();

        var init = await Post(http, server.Port, Rpc(1, "initialize", "{\"protocolVersion\":\"2025-06-18\",\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}"), write);
        Assert.Equal(HttpStatusCode.OK, init.Status);
        Assert.Equal("whisper-shortcut", init.Body!["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        var list = await Post(http, server.Port, Rpc(2, "tools/list"), write);
        var names = list.Body!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("get_configuration", names);
        Assert.Contains("add_glossary_terms", names);
        Assert.Contains("update_settings", names);

        var cfg = await Post(http, server.Port, Rpc(3, "tools/call", "{\"name\":\"get_configuration\",\"arguments\":{}}"), write);
        var cfgJson = JsonNode.Parse(cfg.Body!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.NotNull(cfgJson["tune_recipe"]);

        var add = await Post(http, server.Port, Rpc(4, "tools/call", "{\"name\":\"add_glossary_terms\",\"arguments\":{\"terms\":[\"Gödde\"],\"rationale\":\"Name des Nutzers\"}}"), write);
        Assert.False(add.Body!["result"]!["isError"]!.GetValue<bool>());
        var addJson = JsonNode.Parse(add.Body["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal("applied", addJson["status"]!.GetValue<string>());
        Assert.Equal(new[] { "Gödde" }, s.ReloadSettings().Glossary);

        var notification = await Post(http, server.Port, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", write);
        Assert.Equal(HttpStatusCode.Accepted, notification.Status);
    }

    [Fact]
    public async Task ReadToken_HasNoWriteTools_AndCannotCallThem()
    {
        using var s = new McpStack(McpAccessLevel.Apply);
        using var server = Start(s, "r-token", "w-token");
        using var http = new HttpClient();
        var list = await Post(http, server.Port, Rpc(1, "tools/list"), "r-token");
        var names = list.Body!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("add_glossary_terms", names);
        var call = await Post(http, server.Port, Rpc(2, "tools/call", "{\"name\":\"add_glossary_terms\",\"arguments\":{\"terms\":[\"X\"],\"rationale\":\"r\"}}"), "r-token");
        Assert.True(call.Body!["result"]!["isError"]!.GetValue<bool>());
        Assert.Empty(s.ReloadSettings().Glossary);
    }

    [Fact]
    public async Task NoToken_Is401_AndGet_Is405()
    {
        using var s = new McpStack();
        using var server = Start(s, "r-token", "w-token");
        using var http = new HttpClient();
        var none = await Post(http, server.Port, Rpc(1, "ping"), null);
        Assert.Equal(HttpStatusCode.Unauthorized, none.Status);

        using var get = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/mcp");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "w-token");
        using var resp = await http.SendAsync(get);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
        Assert.Contains("POST", resp.Content.Headers.Allow);
    }

    [Fact]
    public async Task ChunkedBody_IsAccepted()
    {
        using var s = new McpStack();
        using var server = Start(s, "r-token", "w-token");
        var body = Rpc(1, "ping");
        var req = $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nAuthorization: Bearer w-token\r\nTransfer-Encoding: chunked\r\nContent-Type: application/json\r\n\r\n" +
                  $"{Encoding.UTF8.GetByteCount(body):x}\r\n{body}\r\n0\r\n\r\n";
        var resp = await RawExchange(server.Port, req);
        Assert.StartsWith("HTTP/1.1 200 OK", resp);
        Assert.Contains("\"result\"", resp);
        Assert.Contains("Connection: close", resp);
    }

    [Fact]
    public async Task OversizedBody_Is413_AndGarbage_Is400()
    {
        using var s = new McpStack();
        using var server = Start(s, "r-token", "w-token");
        var big = await RawExchange(server.Port, $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: {LoopbackHttpServer.MaxBodyBytes + 1}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 413", big);
        var bad = await RawExchange(server.Port, "this is not http\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", bad);
    }

    [Fact]
    public async Task HandlerException_Is500()
    {
        using var server = new LoopbackHttpServer(0, _ => throw new InvalidOperationException("x"));
        var resp = await RawExchange(server.Port, "GET / HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 500", resp);
    }

    [Fact]
    public async Task AfterDispose_ConnectingFails_AndPortInUseThrows()
    {
        using var s = new McpStack();
        var server = Start(s, "r", "w");
        var port = server.Port;
        var ex = Assert.Throws<SocketException>(() => new LoopbackHttpServer(port, _ => McpHttpResponse.Empty(200)));
        Assert.Equal(SocketError.AddressAlreadyInUse, ex.SocketErrorCode);
        server.Dispose();
        server.Dispose(); // idempotent
        using var http = new HttpClient();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Post(http, port, Rpc(1, "ping"), "w"));
    }
}
