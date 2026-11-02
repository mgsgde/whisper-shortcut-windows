using WhisperShortcut.Core;
using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests.Mcp;

public class McpConfigTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "wsw-mcpcfg-" + Guid.NewGuid().ToString("N"), "mcp.json");

    [Fact]
    public void Tokens_AreCreated_Distinct_Hex_AndNotPlainOnDisk()
    {
        var path = TempFile();
        var c = McpConfig.Load(path, new ReverseProtector());
        var r = c.ReadToken;
        var w = c.WriteToken;
        Assert.Matches("^[0-9a-f]{64}$", r);
        Assert.Matches("^[0-9a-f]{64}$", w);
        Assert.NotEqual(r, w);
        Assert.Equal(r, c.ReadToken);
        var disk = File.ReadAllText(path);
        Assert.DoesNotContain(r, disk);
        Assert.DoesNotContain(w, disk);
        Assert.Contains("readTokenProtected", disk);
        Assert.Contains("writeTokenProtected", disk);

        var again = McpConfig.Load(path, new ReverseProtector());
        Assert.Equal(r, again.ReadToken);
        Assert.Equal(w, again.WriteToken);

        again.RegenerateTokens();
        Assert.NotEqual(r, again.ReadToken);
        Assert.NotEqual(w, again.WriteToken);
        Assert.Equal(again.ReadToken, McpConfig.Load(path, new ReverseProtector()).ReadToken);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Defaults_AndRoundTrip()
    {
        var path = TempFile();
        var c = McpConfig.Load(path, new PlainSecretProtector());
        Assert.False(c.Enabled);
        Assert.Equal(McpConfig.DefaultPort, c.Port);
        Assert.Equal(McpAccessLevel.Propose, c.AccessLevel);
        var changed = 0;
        c.Changed += () => changed++;
        c.Enabled = true;
        c.Port = 50000;
        c.AccessLevel = McpAccessLevel.Apply;
        c.AccessLevel = McpAccessLevel.Apply; // no change, no event
        Assert.Equal(3, changed);
        var d = McpConfig.Load(path, new PlainSecretProtector());
        Assert.True(d.Enabled);
        Assert.Equal(50000, d.Port);
        Assert.Equal(McpAccessLevel.Apply, d.AccessLevel);
        Assert.Contains("\"accessLevel\": \"apply\"", File.ReadAllText(path));
        foreach (var l in new[] { McpAccessLevel.Read, McpAccessLevel.Propose })
        {
            d.AccessLevel = l;
            Assert.Equal(l, McpConfig.Load(path, new PlainSecretProtector()).AccessLevel);
        }
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void InvalidPort_FallsBackToDefault_AndCorruptFileIsKept()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"enabled\":true,\"port\":80}");
        var c = McpConfig.Load(path, new PlainSecretProtector());
        Assert.True(c.Enabled);
        Assert.Equal(McpConfig.DefaultPort, c.Port);

        File.WriteAllText(path, "{broken");
        var d = McpConfig.Load(path, new PlainSecretProtector());
        Assert.False(d.Enabled);
        Assert.True(File.Exists(path + ".bak"));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void TokenThatCannotBeUnprotected_IsReplaced()
    {
        var path = TempFile();
        var c = McpConfig.Load(path, new PlainSecretProtector());
        var old = c.ReadToken;
        var other = McpConfig.Load(path, new NullProtector());
        Assert.NotEqual(old, other.ReadToken);
        Assert.Matches("^[0-9a-f]{64}$", other.ReadToken);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    private sealed class NullProtector : WhisperShortcut.Core.ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string? Unprotect(string stored) => null;
    }

    [Fact]
    public void RecordClient_Upserts_NewestFirst_AndKeepsTen()
    {
        var path = TempFile();
        var c = McpConfig.Load(path, new PlainSecretProtector());
        var t = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        c.RecordClient("Claude Desktop", "1.0", McpAccess.Read, t);
        c.RecordClient("Claude Code", "2.0", McpAccess.Write, t.AddMinutes(1));
        c.RecordClient("  ", "x", McpAccess.Read, t);
        c.RecordClient("Claude Desktop", "1.1", McpAccess.Write, t.AddMinutes(2));
        Assert.Equal(new[] { "Claude Desktop", "Claude Code" }, c.Clients.Select(x => x.Name));
        Assert.Equal("1.1", c.Clients[0].Version);
        Assert.Equal(McpAccess.Write, c.Clients[0].Access);
        Assert.Equal("Claude Code", c.ClientMatching("claude c")!.Name);
        Assert.Null(c.ClientMatching("Cursor"));

        for (var i = 0; i < 12; i++) c.RecordClient("app" + i, "1", McpAccess.Read, t.AddHours(i));
        Assert.Equal(McpConfig.MaxClients, c.Clients.Count);
        Assert.Equal("app11", c.Clients[0].Name);
        Assert.Equal(c.Clients.Select(x => x.Name), McpConfig.Load(path, new PlainSecretProtector()).Clients.Select(x => x.Name));
        Assert.Equal(64, c.Clients.Count == 0 ? 0 : 64);

        c.RecordClient(new string('n', 100), "1", McpAccess.Read, t);
        Assert.Equal(64, c.Clients[0].Name.Length);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Tokens_AreSafeUnderConcurrentFirstAccess()
    {
        var path = TempFile();
        var c = McpConfig.Load(path, new PlainSecretProtector());
        var tokens = new string[16];
        Parallel.For(0, tokens.Length, i => tokens[i] = c.WriteToken);
        Assert.Single(tokens.Distinct());
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}
