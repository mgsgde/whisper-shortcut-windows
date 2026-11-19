using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests.Mcp;

/// <summary>Which port the MCP server tries when 47823 is taken (Mac MCPServerPortTests).</summary>
public class McpPortsTests
{
    private static int[] Window() => Enumerable.Range(McpConfig.DefaultPort, McpPorts.FallbackCount + 1).ToArray();

    [Fact]
    public void An_explicit_override_is_the_only_candidate()
    {
        Assert.Equal(new[] { 47999 }, McpPorts.CandidatePorts(47999, sticky: 47830, fallbackEnabled: true));
        Assert.Equal(new[] { 1 }, McpPorts.CandidatePorts(1, sticky: 0, fallbackEnabled: false));
    }

    [Fact]
    public void Fallback_disabled_binds_only_the_default()
    {
        Assert.Equal(new[] { 47823 }, McpPorts.CandidatePorts(0, sticky: 47830, fallbackEnabled: false));
        Assert.Equal(47823, McpConfig.DefaultPort);
    }

    [Fact]
    public void The_default_list_is_the_default_port_plus_the_next_16()
    {
        var ports = McpPorts.CandidatePorts(0, sticky: 0, fallbackEnabled: true);
        Assert.Equal(17, ports.Count);
        Assert.Equal(47823, ports[0]);
        Assert.Equal(Window(), ports);
    }

    [Fact]
    public void A_sticky_port_in_range_is_tried_first_and_not_repeated()
    {
        var ports = McpPorts.CandidatePorts(0, sticky: 47830, fallbackEnabled: true);
        Assert.Equal(47830, ports[0]);
        Assert.Equal(47823, ports[1]);
        Assert.Equal(1, ports.Count(p => p == 47830));
        Assert.Equal(17, ports.Count);
        var expected = new[] { 47830 }.Concat(Window().Where(p => p != 47830)).ToArray();
        Assert.Equal(expected, ports);
    }

    [Fact]
    public void A_sticky_port_outside_the_window_is_ignored()
    {
        Assert.Equal(Window(), McpPorts.CandidatePorts(0, sticky: 5000, fallbackEnabled: true));
    }

    [Fact]
    public void A_saved_port_outside_the_window_is_an_override_and_one_inside_is_sticky()
    {
        Assert.Equal(new[] { 50000 }, McpPorts.ForConfigured(50000));
        Assert.Equal(47830, McpPorts.ForConfigured(47830)[0]);
        Assert.Equal(47823, McpPorts.ForConfigured(McpConfig.DefaultPort)[0]);
        Assert.Equal(17, McpPorts.ForConfigured(McpConfig.DefaultPort).Count);
    }
}
