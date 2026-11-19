using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisperShortcut.Core.Mcp;

/// <summary>Pure functions (and one file write) behind the "Verbinden" buttons for Claude Desktop and Claude Code.</summary>
public static class McpClientSetup
{
    /// <summary>Key under mcpServers.</summary>
    public const string ServerName = "whisper-shortcut";

    /// <summary>The npx bridge Claude Desktop needs for an HTTP server; same pin as the macOS app.</summary>
    public const string McpRemotePackage = "mcp-remote@0.14.3";

    /// <summary>Suffix of the copy made before a config file is rewritten.</summary>
    public const string BackupSuffix = ".whispershortcut-backup";

    private const string InvalidSubject = "The configuration file";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// %APPDATA%\Claude\claude_desktop_config.json, or the Microsoft Store (MSIX) copy
    /// %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json when that Claude folder exists.
    /// </summary>
    public static string ClaudeDesktopConfigPath(string appData, string localAppData)
    {
        const string file = "claude_desktop_config.json";
        try
        {
            var packages = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var dir in Directory.GetDirectories(packages, "Claude_*").OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var claude = Path.Combine(dir, "LocalCache", "Roaming", "Claude");
                    if (Directory.Exists(claude)) return Path.Combine(claude, file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Path.Combine(appData, "Claude", file);
    }

    /// <summary>Claude Code's user-scope config: userProfile\.claude.json.</summary>
    public static string ClaudeCodeUserConfigPath(string userProfile) => Path.Combine(userProfile, ".claude.json");

    private static JsonObject Merge(string? existingJson, JsonObject entry)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(existingJson)) root = new JsonObject();
        else
        {
            try { root = JsonNode.Parse(existingJson, null, ReadOptions) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { throw new McpConfigException(InvalidSubject + " is not valid JSON, so it was left untouched. Fix or remove the file and try again."); }
        }
        JsonObject servers;
        if (root["mcpServers"] is null) { servers = new JsonObject(); root["mcpServers"] = servers; }
        else servers = root["mcpServers"] as JsonObject
            ?? throw new McpConfigException(InvalidSubject + " has an mcpServers entry that is not an object, so it was left untouched. Fix or remove the file and try again.");
        servers[ServerName] = entry;
        return root;
    }

    private static string Render(JsonNode node) => node.ToJsonString(WriteOptions) + "\n";

    private static JsonObject DesktopEntry(string url, string token) => new()
    {
        ["command"] = "npx",
        ["args"] = new JsonArray("-y", McpRemotePackage, url, "--header", "Authorization:${AUTH_HEADER}"),
        // The header goes through env: Claude Desktop on Windows splits arguments at spaces.
        ["env"] = new JsonObject { ["AUTH_HEADER"] = "Bearer " + token },
    };

    /// <summary>
    /// Adds or replaces mcpServers.whisper-shortcut in the Claude Desktop config; every other server and top-level key stays
    /// (order too). Null/blank input starts a new object; anything that is not a JSON object throws <see cref="McpConfigException"/>.
    /// </summary>
    public static string MergeClaudeDesktop(string? existingJson, string url, string token) =>
        Render(Merge(existingJson, DesktopEntry(url, token)));

    /// <summary>
    /// True for <c>http://127.0.0.1:&lt;p&gt;/mcp</c> and <c>http://localhost:&lt;p&gt;/mcp</c> when
    /// <c>p</c> is <see cref="McpConfig.DefaultPort"/> through the fallback window (Mac isOwnLoopbackMCPURL).
    /// </summary>
    public static bool IsOwnLoopbackMcpUrl(string url)
    {
        const string suffix = "/mcp";
        string? prefix = url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) ? "http://127.0.0.1:"
            : url.StartsWith("http://localhost:", StringComparison.Ordinal) ? "http://localhost:"
            : null;
        if (prefix == null || !url.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var portText = url[prefix.Length..^suffix.Length];
        if (!ushort.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port))
            return false;
        if (port.ToString(System.Globalization.CultureInfo.InvariantCulture) != portText) return false;
        return port >= McpConfig.DefaultPort && port <= McpPorts.LastPort;
    }

    /// <summary>
    /// New config text when our entry's URL is one of our loopback ports but not <paramref name="currentUrl"/>.
    /// Null when there is nothing to change: no entry, the URL is already current, a foreign URL, or the file
    /// does not parse. The written entry uses <paramref name="currentUrl"/> and the bearer token already in
    /// the entry. <paramref name="token"/> is only the fallback when the entry has none — a port change must
    /// not replace the token the user connected with (that one follows the access level they chose then).
    /// </summary>
    public static string? RewriteOwnUrl(string? existingJson, string currentUrl, string token, bool claudeCode)
    {
        if (string.IsNullOrWhiteSpace(existingJson)) return null;
        JsonObject? root;
        try { root = JsonNode.Parse(existingJson, null, ReadOptions) as JsonObject; }
        catch (JsonException) { return null; }
        if (root?["mcpServers"]?[ServerName] is not JsonObject entry) return null;
        var found = EntryUrl(entry);
        if (found == null || found == currentUrl || !IsOwnLoopbackMcpUrl(found)) return null;
        var kept = EntryToken(entry) ?? token;
        return claudeCode ? MergeClaudeCode(existingJson, currentUrl, kept) : MergeClaudeDesktop(existingJson, currentUrl, kept);
    }

    /// <summary>Bearer token already stored on our entry (Claude Code header or Claude Desktop env), without the prefix.</summary>
    private static string? EntryToken(JsonObject entry)
    {
        var header = TokenText(entry["headers"]?["Authorization"]) ?? TokenText(entry["env"]?["AUTH_HEADER"]);
        const string prefix = "Bearer ";
        if (header == null || !header.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var value = header[prefix.Length..];
        return value.Length == 0 ? null : value;
    }

    private static string? TokenText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string? EntryUrl(JsonObject entry)
    {
        if (entry["url"] is JsonValue url && url.TryGetValue<string>(out var direct) && direct.Length > 0) return direct;
        if (entry["args"] is JsonArray args)
        {
            foreach (var arg in args)
                if (arg is JsonValue value && value.TryGetValue<string>(out var text)
                    && (text.StartsWith("http://", StringComparison.Ordinal) || text.StartsWith("https://", StringComparison.Ordinal)))
                    return text;
        }
        return null;
    }

    /// <summary>Same for Claude Code's .claude.json (a direct HTTP server).</summary>
    public static string MergeClaudeCode(string? existingJson, string url, string token) =>
        Render(Merge(existingJson, new JsonObject
        {
            ["type"] = "http",
            ["url"] = url,
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
        }));

    /// <summary>
    /// The config without mcpServers.whisper-shortcut (uninstall), everything else untouched. Null when there is nothing
    /// to remove or the file is not a JSON object: then the caller must not write.
    /// </summary>
    public static string? RemoveServer(string? existingJson)
    {
        if (string.IsNullOrWhiteSpace(existingJson)) return null;
        JsonObject? root;
        try { root = JsonNode.Parse(existingJson, null, ReadOptions) as JsonObject; }
        catch (JsonException) { return null; }
        if (root?["mcpServers"] is not JsonObject servers || !servers.Remove(ServerName)) return null;
        return Render(root);
    }

    /// <summary>Arguments for `claude mcp add` (user scope).</summary>
    public static IReadOnlyList<string> ClaudeCodeAddArguments(string url, string token) =>
        new[] { "mcp", "add", "--transport", "http", "--scope", "user", ServerName, url, "--header", "Authorization: Bearer " + token };

    /// <summary>Arguments for `claude mcp remove` (user scope).</summary>
    public static IReadOnlyList<string> ClaudeCodeRemoveArguments() =>
        new[] { "mcp", "remove", "--scope", "user", ServerName };

    /// <summary>For "Von Hand einrichten": the Claude Desktop JSON block and the Claude Code command line, with short German headings.</summary>
    public static string ManualSetupText(string url, string token)
    {
        var block = new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = DesktopEntry(url, token) } };
        var sb = new StringBuilder();
        sb.Append("Claude Desktop: in die Datei claude_desktop_config.json eintragen (Einstellungen > Entwickler > Konfiguration bearbeiten):\n\n");
        sb.Append(block.ToJsonString(WriteOptions)).Append("\n\n");
        sb.Append("Claude Code: in einer Eingabeaufforderung ausführen:\n\n");
        sb.Append("claude mcp add --transport http --scope user ").Append(ServerName).Append(' ').Append(url)
          .Append(" --header \"Authorization: Bearer ").Append(token).Append("\"\n");
        return sb.ToString();
    }

    /// <summary>
    /// Reads <paramref name="path"/> (if present), keeps a copy at path + <see cref="BackupSuffix"/> the first time only,
    /// and writes merge(existing) atomically (tmp + move), creating the folder. The merge runs first, so an invalid file
    /// stays untouched. A later Connect must not replace that first copy: it is the user's original (Mac #409).
    /// Throws <see cref="McpConfigException"/> / IOException.
    /// </summary>
    public static void WriteWithBackup(string path, Func<string?, string> merge)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        string merged;
        try { merged = merge(existing); }
        catch (McpConfigException ex) when (ex.Message.StartsWith(InvalidSubject, StringComparison.Ordinal))
        {
            throw new McpConfigException(Path.GetFileName(path) + ex.Message[InvalidSubject.Length..]);
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var backup = path + BackupSuffix;
        if (existing != null && !File.Exists(backup)) File.Copy(path, backup, overwrite: false);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, merged, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
