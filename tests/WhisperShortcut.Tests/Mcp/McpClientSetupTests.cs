using System.Text.Json.Nodes;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests.Mcp;

public class McpClientSetupTests
{
    private const string Url = "http://127.0.0.1:47823/mcp";

    [Fact]
    public void MergeClaudeDesktop_KeepsOtherServersAndKeys_AndAddsOurs()
    {
        var existing = "{\"globalShortcut\":\"Ctrl+Space\",\"mcpServers\":{\"other\":{\"command\":\"x\"}},\"theme\":\"dark\"}";
        var merged = JsonNode.Parse(McpClientSetup.MergeClaudeDesktop(existing, Url, "tok"))!;
        Assert.Equal("Ctrl+Space", merged["globalShortcut"]!.GetValue<string>());
        Assert.Equal("dark", merged["theme"]!.GetValue<string>());
        Assert.Equal("x", merged["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        var ours = merged["mcpServers"]![McpClientSetup.ServerName]!;
        Assert.Equal("npx", ours["command"]!.GetValue<string>());
        Assert.Equal(new[] { "-y", "mcp-remote@0.14.3", Url, "--header", "Authorization:${AUTH_HEADER}" }, ours["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal("Bearer tok", ours["env"]!["AUTH_HEADER"]!.GetValue<string>());
        Assert.Equal(new[] { "globalShortcut", "mcpServers", "theme" }, ((JsonObject)merged).Select(p => p.Key));
    }

    [Fact]
    public void MergeClaudeDesktop_ReplacesOlderEntry_KeepingPosition()
    {
        var existing = "{\"mcpServers\":{\"a\":{},\"whisper-shortcut\":{\"command\":\"old\"},\"b\":{}}}";
        var merged = (JsonObject)JsonNode.Parse(McpClientSetup.MergeClaudeDesktop(existing, Url, "tok"))!;
        var servers = (JsonObject)merged["mcpServers"]!;
        Assert.Equal(new[] { "a", "whisper-shortcut", "b" }, servers.Select(p => p.Key));
        Assert.Equal("npx", servers["whisper-shortcut"]!["command"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public void Merge_EmptyInput_StartsNewObject(string? existing)
    {
        var text = McpClientSetup.MergeClaudeDesktop(existing, Url, "tok");
        Assert.EndsWith("\n", text);
        Assert.NotNull(JsonNode.Parse(text)!["mcpServers"]![McpClientSetup.ServerName]);
        var code = JsonNode.Parse(McpClientSetup.MergeClaudeCode(existing, Url, "tok"))!["mcpServers"]![McpClientSetup.ServerName]!;
        Assert.Equal("http", code["type"]!.GetValue<string>());
        Assert.Equal(Url, code["url"]!.GetValue<string>());
        Assert.Equal("Bearer tok", code["headers"]!["Authorization"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("{\"mcpServers\":5}")]
    public void Merge_InvalidJson_Throws(string existing)
    {
        var ex = Assert.Throws<McpConfigException>(() => McpClientSetup.MergeClaudeDesktop(existing, Url, "tok"));
        Assert.Contains("left untouched", ex.Message);
    }

    [Fact]
    public void Merge_ToleratesTrailingCommasAndComments()
    {
        var existing = "{\n // my servers\n \"mcpServers\": { \"other\": {}, },\n}";
        var merged = JsonNode.Parse(McpClientSetup.MergeClaudeDesktop(existing, Url, "tok"))!;
        Assert.NotNull(merged["mcpServers"]!["other"]);
        Assert.NotNull(merged["mcpServers"]![McpClientSetup.ServerName]);
    }

    [Fact]
    public void WriteWithBackup_WritesBackupOfOldContent_AndNewFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wsw-setup-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "Claude", "claude_desktop_config.json");
        McpClientSetup.WriteWithBackup(path, old => McpClientSetup.MergeClaudeDesktop(old, Url, "t1"));
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + McpClientSetup.BackupSuffix));
        var first = File.ReadAllText(path);

        McpClientSetup.WriteWithBackup(path, old => McpClientSetup.MergeClaudeDesktop(old, Url, "t2"));
        Assert.Equal(first, File.ReadAllText(path + McpClientSetup.BackupSuffix));
        Assert.Contains("Bearer t2", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));

        File.WriteAllText(path, "{broken");
        var ex = Assert.Throws<McpConfigException>(() => McpClientSetup.WriteWithBackup(path, old => McpClientSetup.MergeClaudeDesktop(old, Url, "t3")));
        Assert.StartsWith("claude_desktop_config.json is not valid JSON", ex.Message);
        Assert.Equal("{broken", File.ReadAllText(path));
        Assert.Equal(first, File.ReadAllText(path + McpClientSetup.BackupSuffix));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ClaudeDesktopConfigPath_PrefersAnExistingMsixFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "wsw-paths-" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "Roaming");
        var local = Path.Combine(root, "Local");
        Assert.Equal(Path.Combine(appData, "Claude", "claude_desktop_config.json"), McpClientSetup.ClaudeDesktopConfigPath(appData, local));
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Claude_abc123", "LocalCache", "Local"));
        Assert.Equal(Path.Combine(appData, "Claude", "claude_desktop_config.json"), McpClientSetup.ClaudeDesktopConfigPath(appData, local)); // no Roaming\Claude yet
        var msix = Path.Combine(local, "Packages", "Claude_abc123", "LocalCache", "Roaming", "Claude");
        Directory.CreateDirectory(msix);
        Assert.Equal(Path.Combine(msix, "claude_desktop_config.json"), McpClientSetup.ClaudeDesktopConfigPath(appData, local));
        Directory.Delete(root, true);
    }

    [Fact]
    public void ClaudeCode_Paths_AndArguments()
    {
        Assert.Equal(Path.Combine("C:\\Users\\a", ".claude.json"), McpClientSetup.ClaudeCodeUserConfigPath("C:\\Users\\a"));
        Assert.Equal(new[] { "mcp", "add", "--transport", "http", "--scope", "user", "whisper-shortcut", Url, "--header", "Authorization: Bearer tok" },
            McpClientSetup.ClaudeCodeAddArguments(Url, "tok"));
        Assert.Equal(new[] { "mcp", "remove", "--scope", "user", "whisper-shortcut" }, McpClientSetup.ClaudeCodeRemoveArguments());
        var manual = McpClientSetup.ManualSetupText(Url, "tok");
        Assert.Contains("claude mcp add --transport http --scope user whisper-shortcut " + Url, manual);
        Assert.Contains("mcp-remote@0.14.3", manual);
    }
}
