using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisperShortcut.Core.Mcp;

/// <summary>An AI app that has connected, remembered for the "connected apps" list in Settings.</summary>
/// <param name="Name">clientInfo.name.</param>
/// <param name="Version">clientInfo.version.</param>
/// <param name="Access">Which token it used.</param>
/// <param name="LastConnectedUtc">Time of its last initialize.</param>
public sealed record McpConnectedClient(string Name, string Version, McpAccess Access, DateTime LastConnectedUtc);

/// <summary>
/// The MCP server switch, port, access level, tokens and connected clients, in their own file (mcp.json) so
/// settings.json stays untouched. Thread-safe: requests arrive on thread-pool threads.
/// </summary>
public sealed class McpConfig
{
    /// <summary>Default port of the local server.</summary>
    public const int DefaultPort = 47823;

    /// <summary>Most clients remembered.</summary>
    public const int MaxClients = 10;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private bool _enabled;
    private int _port = DefaultPort;
    private McpAccessLevel _level = McpAccessLevel.Propose;
    private string? _readToken;
    private string? _writeToken;
    private List<McpConnectedClient> _clients = new();

    private McpConfig(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    /// <summary>Default location: mcp.json in <see cref="AppData.SettingsFolder"/>.</summary>
    public static string DefaultPath => Path.Combine(AppData.SettingsFolder, "mcp.json");

    /// <summary>Raised after any change, on the calling thread (which may be a request thread: subscribers marshal to the UI themselves).</summary>
    public event Action? Changed;

    internal static string LevelName(McpAccessLevel level) => level switch
    {
        McpAccessLevel.Read => "read",
        McpAccessLevel.Apply => "apply",
        _ => "propose",
    };

    private static McpAccessLevel ParseLevel(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "read" => McpAccessLevel.Read,
        "apply" => McpAccessLevel.Apply,
        _ => McpAccessLevel.Propose,
    };

    /// <summary>Loads mcp.json; a missing or corrupt file yields defaults (a corrupt file is kept as .bak).</summary>
    public static McpConfig Load(string path, ISecretProtector protector)
    {
        var c = new McpConfig(path, protector);
        if (!File.Exists(path)) return c;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) throw new JsonException("not an object");
            c._enabled = o["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var en) && en;
            if (o["port"] is JsonValue pv && pv.TryGetValue<int>(out var port) && port is >= 1024 and <= 65535) c._port = port;
            c._level = ParseLevel(Str(o["accessLevel"]));
            c._readToken = Unprotect(protector, Str(o["readTokenProtected"]));
            c._writeToken = Unprotect(protector, Str(o["writeTokenProtected"]));
            if (o["clients"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject co || Str(co["name"]) is not { Length: > 0 } name) continue;
                    var access = Str(co["access"]) == "write" ? McpAccess.Write : McpAccess.Read;
                    var when = DateTime.TryParse(Str(co["lastConnected"]), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : DateTime.MinValue;
                    c._clients.Add(new McpConnectedClient(name, Str(co["version"]) ?? "", access, DateTime.SpecifyKind(when, DateTimeKind.Utc)));
                    if (c._clients.Count == MaxClients) break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("mcp.json unreadable, using defaults", ex);
            try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
            c = new McpConfig(path, protector);
        }
        return c;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // A token that cannot be unprotected (other Windows user, damaged file) is dropped; the getter then mints a
    // new one, so apps connected with the old key have to be connected again.
    private static string? Unprotect(ISecretProtector p, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            var plain = p.Unprotect(stored);
            return string.IsNullOrEmpty(plain) ? null : plain;
        }
        catch { return null; }
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Whether the local server should run. Saved when set.</summary>
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set { if (Update(() => { var ch = _enabled != value; _enabled = value; return ch; })) Changed?.Invoke(); }
    }

    /// <summary>TCP port on 127.0.0.1. Saved when set; <see cref="Load"/> replaces invalid stored values with the default.</summary>
    public int Port
    {
        get { lock (_gate) return _port; }
        set { if (Update(() => { var ch = _port != value; _port = value; return ch; })) Changed?.Invoke(); }
    }

    /// <summary>What AI apps may do. Saved when set.</summary>
    public McpAccessLevel AccessLevel
    {
        get { lock (_gate) return _level; }
        set { if (Update(() => { var ch = _level != value; _level = value; return ch; })) Changed?.Invoke(); }
    }

    // Applies a change under the lock and saves when something changed.
    private bool Update(Func<bool> change)
    {
        lock (_gate)
        {
            if (!change()) return false;
            TrySave();
            return true;
        }
    }

    /// <summary>Token of read-only connections; created on first access (32 random bytes, hex) and saved.</summary>
    public string ReadToken => Token(ref _readToken);

    /// <summary>Token of connections that may change settings; separate from <see cref="ReadToken"/>.</summary>
    public string WriteToken => Token(ref _writeToken);

    private string Token(ref string? field)
    {
        bool created;
        string value;
        lock (_gate)
        {
            created = string.IsNullOrEmpty(field);
            if (created)
            {
                field = NewToken();
                TrySave();
            }
            value = field!;
        }
        if (created) Changed?.Invoke();
        return value;
    }

    /// <summary>Replaces both tokens; every connected app has to be connected again.</summary>
    public void RegenerateTokens()
    {
        lock (_gate)
        {
            _readToken = NewToken();
            _writeToken = NewToken();
            TrySave();
        }
        Changed?.Invoke();
    }

    /// <summary>Connected apps, newest first.</summary>
    public IReadOnlyList<McpConnectedClient> Clients
    {
        get { lock (_gate) return _clients.ToList(); }
    }

    /// <summary>Remembers an app (upsert by name, newest first, at most <see cref="MaxClients"/>) and saves.</summary>
    public void RecordClient(string name, string version, McpAccess access, DateTime utcNow)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return;
        if (n.Length > 64) n = n[..64];
        var v = (version ?? "").Trim();
        if (v.Length > 64) v = v[..64];
        lock (_gate)
        {
            _clients.RemoveAll(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));
            _clients.Insert(0, new McpConnectedClient(n, v, access, DateTime.SpecifyKind(utcNow.ToUniversalTime(), DateTimeKind.Utc)));
            if (_clients.Count > MaxClients) _clients.RemoveRange(MaxClients, _clients.Count - MaxClients);
            TrySave();
        }
        Changed?.Invoke();
    }

    /// <summary>The newest client whose name starts with <paramref name="namePrefix"/> (case-insensitive).</summary>
    public McpConnectedClient? ClientMatching(string namePrefix)
    {
        lock (_gate)
            return _clients.FirstOrDefault(c => c.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase));
    }

    // Request threads must not fail because the disk does.
    private void TrySave()
    {
        try { Save(); }
        catch (Exception ex) { Log.Error("mcp.json could not be saved", ex); }
    }

    /// <summary>Writes mcp.json atomically. Tokens are stored only in protected form. No-op after "Zurücksetzen".</summary>
    public void Save()
    {
        if (AppData.ResetDone) return;
        string json;
        lock (_gate)
        {
            var o = new JsonObject
            {
                ["enabled"] = _enabled,
                ["port"] = _port,
                ["accessLevel"] = LevelName(_level),
            };
            if (!string.IsNullOrEmpty(_readToken)) o["readTokenProtected"] = _protector.Protect(_readToken);
            if (!string.IsNullOrEmpty(_writeToken)) o["writeTokenProtected"] = _protector.Protect(_writeToken);
            var arr = new JsonArray();
            foreach (var c in _clients)
                arr.Add(new JsonObject
                {
                    ["name"] = c.Name,
                    ["version"] = c.Version,
                    ["access"] = c.Access == McpAccess.Write ? "write" : "read",
                    ["lastConnected"] = c.LastConnectedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                });
            o["clients"] = arr;
            json = o.ToJsonString(WriteOptions);
        }
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        lock (_gate)
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
