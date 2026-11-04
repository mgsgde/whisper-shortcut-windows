using System.Text.Json.Nodes;
using AppLog = WhisperShortcut.Core.Log;

namespace WhisperShortcut.Core.Mcp;

/// <summary>
/// get_settings and update_settings over <see cref="McpSettingsCatalog"/>. A separate provider on purpose: it shows how
/// providers are registered next to <see cref="McpConfigTools"/> without touching it.
/// </summary>
public sealed class McpSettingsTools(McpToolContext ctx) : IMcpToolProvider
{
    private const int MaxChangesPerCall = 20;

    /// <inheritdoc />
    public IEnumerable<JsonObject> ToolDefinitions(McpAccess access)
    {
        yield return new JsonObject
        {
            ["name"] = "get_settings",
            ["description"] = "Lists the WhisperShortcut settings an AI app can read and, within the user's access level, change with update_settings: current value, type, and for enums the same options the user sees in Settings. Optionally filter by category (\"models\", \"shortcuts\", \"clipboard\", \"general\").",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["category"] = new JsonObject { ["type"] = "string", ["description"] = "Only settings in this category." } },
            },
            ["annotations"] = new JsonObject { ["title"] = "Get Settings", ["readOnlyHint"] = true, ["openWorldHint"] = false },
        };
        if (access != McpAccess.Write) yield break;
        yield return McpConfigTools.WriteTool("update_settings", "Update Settings",
            $"Changes one or more settings from get_settings. All changes are validated together against the state the whole call would leave; if any one is invalid, nothing is written. Settings that always wait for approval (e.g. Mit Windows starten) turn the whole call into one proposal, even at access level \"apply\". A shortcut value is a string such as \"Ctrl+Shift+D\" ({McpSettingsCatalog.ShortcutFormatHint}). API keys, the microphone and offline mode cannot be changed from AI apps.",
            new JsonObject { ["changes"] = new JsonObject { ["type"] = "object", ["description"] = $"Setting id (from get_settings) → new value. Max {MaxChangesPerCall} per call." } },
            new[] { "changes" }, McpConfigTools.Writes(true));
    }

    /// <inheritdoc />
    public bool Handles(string toolName) => toolName is "get_settings" or "update_settings";

    /// <inheritdoc />
    public McpToolResult Call(string toolName, JsonObject arguments, McpAccess access) => toolName switch
    {
        "get_settings" => GetSettings(arguments),
        "update_settings" => UpdateSettings(arguments, access),
        _ => new McpToolResult("Unknown tool: " + toolName, true),
    };

    private McpToolResult GetSettings(JsonObject args)
    {
        var category = args["category"] is JsonValue v && v.TryGetValue<string>(out var c) && c.Length > 0 ? c : null;
        var list = new JsonArray();
        foreach (var s in ctx.Applier.Catalog.Where(s => category == null || s.Category == category))
        {
            var o = new JsonObject
            {
                ["id"] = s.Id,
                ["title"] = s.Title,
                ["category"] = s.Category,
                ["type"] = s.Type switch { McpSettingType.Bool => "bool", McpSettingType.Enum => "enum", _ => "shortcut" },
                ["value"] = s.Read()?.DeepClone(),
                ["requires_approval"] = s.ProposalOnly,
            };
            if (s.Description != null) o["description"] = s.Description;
            if (s.Type == McpSettingType.Enum)
            {
                var options = new JsonArray();
                foreach (var opt in s.Options?.Invoke() ?? Array.Empty<McpSettingOption>())
                {
                    var oo = new JsonObject { ["value"] = opt.Value, ["label"] = opt.Label, ["available"] = opt.Available };
                    if (opt.Reason != null) oo["reason"] = opt.Reason;
                    options.Add(oo);
                }
                o["options"] = options;
            }
            if (s.Type == McpSettingType.Hotkey) o["format"] = McpSettingsCatalog.ShortcutFormatHint;
            list.Add(o);
        }
        AppLog.Info($"MCP: get_settings read n={list.Count}");
        return new McpToolResult(McpToolContext.Json(new JsonObject { ["settings"] = list }), false);
    }

    private McpToolResult UpdateSettings(JsonObject args, McpAccess access)
    {
        const string tool = "update_settings";
        if (ctx.CheckWrite(tool, access) is { } blocked) return blocked;
        var (rationale, rerr) = ctx.Rationale(args, tool);
        if (rerr != null) return rerr;

        if (args["changes"] is not JsonObject changes || changes.Count == 0)
            return ctx.Reject(tool, "no changes", "changes must be a non-empty object of setting id → new value.");
        if (changes.Count > MaxChangesPerCall)
            return ctx.Reject(tool, "too many changes", $"Send at most {MaxChangesPerCall} changes per call.");

        var catalog = ctx.Applier.Catalog;
        var real = new List<(McpSetting Setting, JsonNode? From, JsonNode To)>();
        foreach (var (id, value) in changes)
        {
            var setting = catalog.FirstOrDefault(s => s.Id == id);
            if (setting == null) return ctx.Reject(tool, "unknown setting", $"Unknown setting id: {id}. Call get_settings first.");
            var problem = setting.Validate(value);
            if (problem != null) return ctx.Reject(tool, "invalid value", $"{setting.Title}: {problem}");
            var to = setting.Normalize(value)!;
            var from = setting.Read();
            if (McpSettingsCatalog.SameValue(from, to)) continue;
            real.Add((setting, from, to));
        }

        if (real.Count == 0)
        {
            AppLog.Info($"MCP: {tool} no_change n=0");
            return new McpToolResult(McpToolContext.Json(new JsonObject { ["status"] = "no_change", ["changed"] = new JsonArray() }), false);
        }

        // The state the whole batch leaves, not each item against today's: two shortcuts may not claim the same keys.
        var batchProblem = McpSettingsCatalog.ValidateBatch(catalog, real.Select(r => (r.Setting.Id, (JsonNode?)r.To)).ToList(),
            McpSettingsCatalog.FixedHotkeys(ctx.Settings));
        if (batchProblem != null) return ctx.Reject(tool, "invalid value", batchProblem);

        var items = real.Select(r => new McpSettingsChangeItem(
            r.Setting.Id, r.Setting.Title, r.Setting.Label(r.From), r.Setting.Label(r.To),
            r.From?.ToJsonString() ?? "null", r.To.ToJsonString())).ToList();
        var summary = new JsonArray();
        foreach (var i in items) summary.Add(new JsonObject { ["id"] = i.Id, ["from"] = i.FromLabel, ["to"] = i.ToLabel });

        try
        {
            if (real.Any(r => r.Setting.ProposalOnly) || ctx.Config.AccessLevel == McpAccessLevel.Propose)
            {
                var p = ctx.Proposals.Create(McpProposalKind.Settings, "Einstellungen ändern", rationale!, settingsChanges: items);
                AppLog.Info($"MCP: {tool} proposed n={items.Count}");
                return ctx.Pending(p);
            }
            var changeId = ctx.Applier.ApplySettings(items, rationale!, false);
            AppLog.Info($"MCP: {tool} applied n={items.Count}");
            return new McpToolResult(McpToolContext.Json(new JsonObject { ["status"] = "applied", ["change_id"] = changeId, ["changed"] = summary }), false);
        }
        catch (McpConfigException ex)
        {
            return ctx.Reject(tool, "failed", ex.Message);
        }
    }
}
