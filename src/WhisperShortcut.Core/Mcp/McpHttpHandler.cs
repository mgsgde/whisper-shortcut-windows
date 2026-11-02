using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisperShortcut.Core.Mcp;

/// <summary>One parsed HTTP request. Header names are case-insensitive (build the dictionary with <see cref="StringComparer.OrdinalIgnoreCase"/>).</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Request target, possibly with a query string.</param>
/// <param name="Headers">Request headers.</param>
/// <param name="Body">Raw body bytes.</param>
public sealed record McpHttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>One HTTP response; Content-Length and Connection are added by the server.</summary>
/// <param name="Status">Status code.</param>
/// <param name="Headers">Response headers.</param>
/// <param name="Body">Body bytes.</param>
public sealed record McpHttpResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A JSON response (Content-Type application/json).</summary>
    public static McpHttpResponse Json(JsonNode node, int status = 200) =>
        new(status, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetBytes(node.ToJsonString(Options)));

    /// <summary>A response without body.</summary>
    public static McpHttpResponse Empty(int status) =>
        new(status, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Array.Empty<byte>());
}

/// <summary>The MCP endpoint (port of MCPServer.handle): origin and host checks, bearer tokens, JSON-RPC batches.</summary>
public static class McpHttpHandler
{
    private static McpHttpResponse WithHeader(McpHttpResponse r, string name, string value)
    {
        var h = new Dictionary<string, string>(r.Headers, StringComparer.OrdinalIgnoreCase) { [name] = value };
        return r with { Headers = h };
    }

    private static McpHttpResponse Err(int status, string message) => McpHttpResponse.Json(new JsonObject { ["error"] = message }, status);

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]" || host == "::1";

    // "127.0.0.1:47823", "localhost", "[::1]:80" -> the host part.
    private static string HostPart(string hostHeader)
    {
        var h = hostHeader.Trim();
        if (h.StartsWith('['))
        {
            var end = h.IndexOf(']');
            return end < 0 ? h : h[..(end + 1)];
        }
        var colon = h.IndexOf(':');
        return colon < 0 ? h : h[..colon];
    }

    // Both comparisons always run (no early exit), on bytes, so timing does not tell which token was close.
    private static bool TokenMatches(string authorization, string token)
    {
        var expected = Encoding.UTF8.GetBytes("Bearer " + token);
        var actual = Encoding.UTF8.GetBytes(authorization);
        var equal = CryptographicOperations.FixedTimeEquals(expected, actual);
        return equal && token.Length > 0;
    }

    /// <summary>Handles one request and returns the response.</summary>
    public static McpHttpResponse Handle(McpHttpRequest request, string readToken, string writeToken, McpDispatcher dispatcher)
    {
        var path = request.Path;
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        if (path != "/mcp") return Err(404, "not found");

        if (request.Headers.TryGetValue("Origin", out var origin))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var u) || !IsLocalHost(u.Host)) return Err(403, "forbidden origin");
        }
        // DNS rebinding: a foreign name pointing at 127.0.0.1 still carries its own Host header.
        if (request.Headers.TryGetValue("Host", out var host) && !IsLocalHost(HostPart(host))) return Err(403, "forbidden host");

        request.Headers.TryGetValue("Authorization", out var auth);
        auth ??= "";
        var isWrite = TokenMatches(auth, writeToken);
        var isRead = TokenMatches(auth, readToken);
        if (!isWrite && !isRead)
            return WithHeader(Err(401, "unauthorized"), "WWW-Authenticate", "Bearer");
        var access = isWrite ? McpAccess.Write : McpAccess.Read;

        if (!request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            return WithHeader(Err(405, "method not allowed"), "Allow", "POST");

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(request.Body); }
        catch (JsonException)
        {
            return McpHttpResponse.Json(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = null,
                ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" },
            });
        }

        try
        {
            if (parsed is JsonArray batch)
            {
                if (batch.Count == 0) return McpHttpResponse.Json(Invalid());
                var responses = new JsonArray();
                foreach (var item in batch.ToList())
                    if (dispatcher.Dispatch(item, access) is { } r) responses.Add(r);
                return responses.Count == 0 ? McpHttpResponse.Empty(202) : McpHttpResponse.Json(responses);
            }
            var single = dispatcher.Dispatch(parsed, access);
            return single == null ? McpHttpResponse.Empty(202) : McpHttpResponse.Json(single);
        }
        catch (Exception ex)
        {
            Log.Error("MCP: dispatch failed", ex);
            return McpHttpResponse.Json(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = null,
                ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "Internal error" },
            }, 500);
        }

        static JsonObject Invalid() => new()
        {
            ["jsonrpc"] = "2.0", ["id"] = null,
            ["error"] = new JsonObject { ["code"] = -32600, ["message"] = "Invalid Request" },
        };
    }
}
