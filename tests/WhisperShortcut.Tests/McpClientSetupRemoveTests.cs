using System.Text.Json.Nodes;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests;

public class McpClientSetupRemoveTests
{
    [Fact]
    public void RemovesOnlyOurEntry()
    {
        var merged = McpClientSetup.MergeClaudeCode("{\"theme\":\"dark\",\"mcpServers\":{\"other\":{\"url\":\"x\"}}}", "http://127.0.0.1:1/mcp", "k");
        var removed = McpClientSetup.RemoveServer(merged);
        Assert.NotNull(removed);
        var root = JsonNode.Parse(removed!)!.AsObject();
        Assert.Equal("dark", (string?)root["theme"]);
        Assert.NotNull(root["mcpServers"]!["other"]);
        Assert.Null(root["mcpServers"]![McpClientSetup.ServerName]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"mcpServers\":{\"other\":{}}}")]
    public void NothingToRemoveReturnsNull(string? json) =>
        Assert.Null(McpClientSetup.RemoveServer(json));
}
