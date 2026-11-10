using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AppLog = WhisperShortcut.Core.Log;

namespace WhisperShortcut.Core.Mcp;

/// <summary>
/// get_configuration, the glossary / instruction / quick instruction tools, report_problem and get_proposal_status,
/// plus the resources (effective prompts, glossary, template). Settings tools live in <see cref="McpSettingsTools"/>.
/// </summary>
public sealed class McpConfigTools(McpToolContext ctx) : IMcpToolProvider, IMcpResourceProvider
{
    /// <summary>Characters the glossary may hold in total (all lines).</summary>
    private const int GlossaryCharBudget = 6000;
    private const int MaxTermsPerCall = 50;
    // Above the built-in default (about 5,400 characters), so an AI app can send a tidied default back.
    private const int MaxPromptCharacters = 10_000;

    private const string DictationUri = "whisper-shortcut://system-prompt/dictation";
    private const string DictatePromptUri = "whisper-shortcut://system-prompt/dictate_prompt";
    private const string GlossaryUri = "whisper-shortcut://glossary";
    private const string TemplateUri = "whisper-shortcut://template";

    private static readonly HashSet<string> ReadTools = new() { "get_configuration" };
    private static readonly HashSet<string> WriteTools = new()
    {
        "add_glossary_terms", "update_system_prompt", "update_quick_instructions", "report_problem", "get_proposal_status",
    };

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // ---- definitions -------------------------------------------------------------------------------------------

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonObject StringList(string description, int? maxItems = null)
    {
        var o = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };
        if (maxItems != null) o["maxItems"] = maxItems;
        return o;
    }

    /// <summary>Builds a write tool; the rationale is added to properties and required.</summary>
    internal static JsonObject WriteTool(string name, string title, string description, JsonObject properties, string[] required, JsonObject annotations)
    {
        properties["rationale"] = Prop("string", "One line (max 300 characters) on why this change helps the user. Shown to the user.");
        annotations["title"] = title;
        annotations["openWorldHint"] = false;
        var req = new JsonArray();
        foreach (var r in required.Append("rationale")) req.Add(r);
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = req },
            ["_meta"] = new JsonObject { ["anthropic/requiresUserInteraction"] = true },
            ["annotations"] = annotations,
        };
    }

    /// <summary>Annotations of a tool that changes things.</summary>
    internal static JsonObject Writes(bool destructive, bool? idempotent = null)
    {
        var o = new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = destructive };
        if (idempotent != null) o["idempotentHint"] = idempotent;
        return o;
    }

    /// <inheritdoc />
    public IEnumerable<JsonObject> ToolDefinitions(McpAccess access)
    {
        yield return new JsonObject
        {
            ["name"] = "get_configuration",
            ["description"] = "Overview of how WhisperShortcut is configured: version, engines and models, shortcuts, which instruction sections are customized, glossary, quick instructions, the access level AI apps have, and the recipe for tuning WhisperShortcut to the user. Call this first when the user wants WhisperShortcut improved.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["include"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("system_prompts", "glossary", "template") },
                        ["description"] = "Also return these full texts inline (for clients without MCP resources).",
                    },
                },
            },
            ["annotations"] = new JsonObject { ["title"] = "Get Configuration", ["readOnlyHint"] = true, ["openWorldHint"] = false },
        };
        if (access != McpAccess.Write) yield break;

        yield return WriteTool("add_glossary_terms", "Add Glossary Terms",
            $"Adds names and domain terms to the glossary every speech engine uses. Only ever adds, never removes. Each term is a bare term or: Term (not \"wrong\", \"wrong2\") with the misheard forms. Max {GlossaryEdit.MaxTermLength} characters and {GlossaryPrompt.MaxTermWords} words per term, at most {MaxTermsPerCall} terms per call; the glossary has a budget of {GlossaryCharBudget} characters. Applied at once, unless the user set AI app access to \"Suggest changes for me to approve\" in WhisperShortcut Settings, in which case it creates a proposal the user reviews there.",
            new JsonObject { ["terms"] = StringList("Terms to add.", MaxTermsPerCall) }, new[] { "terms" }, Writes(false, true));

        yield return WriteTool("update_system_prompt", "Update Instructions",
            $"Replaces one instruction section with the full new text (max {MaxPromptCharacters} characters). Always waits for the user to accept it in WhisperShortcut, at every access level, because it applies to everything the user dictates. \"dictation\" is used only by Gemini speech recognition (Parakeet and OpenAI take no instructions); \"dictate_prompt\" is used by Dictate Prompt on every engine. Read the current text first and change as little as possible.",
            new JsonObject
            {
                ["section"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("dictation", "dictate_prompt") },
                ["content"] = Prop("string", "The complete new text of the section."),
            }, new[] { "section", "content" }, Writes(true));

        yield return WriteTool("update_quick_instructions", "Update Quick Instructions",
            $"Replaces the whole list of quick instructions (Ctrl+1 to Ctrl+{QuickActionList.Max} while recording a Dictate Prompt). At most {QuickActionList.Max} items of {QuickActionList.MaxLength} characters each. Read the current list with get_configuration and keep the ones the user still needs.",
            new JsonObject { ["instructions"] = StringList("The complete new list.", QuickActionList.Max) }, new[] { "instructions" }, Writes(true));

        yield return new JsonObject
        {
            ["name"] = "report_problem",
            ["description"] = "Reports a problem in WhisperShortcut itself that no setting can fix: the instructions already forbid the behavior, or the mistake is in what the app does (pasting, timing, the wrong model). Saves a report file the user reviews; nothing is sent unless the user reports it as a GitHub issue themselves. Remove names, patient and customer data from the examples.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["title"] = Prop("string", "Short summary, max 120 characters."),
                    ["what_happens"] = Prop("string", "What the app does wrong, max 1,000 characters."),
                    ["examples"] = StringList("Up to 3 short redacted excerpts, each max 500 characters.", 3),
                    ["suspected_cause"] = Prop("string", "Optional. Where the bug might be, max 1,000 characters."),
                    ["why_not_settings"] = Prop("string", "Why a setting or instruction change cannot fix it, max 500 characters."),
                },
                ["required"] = new JsonArray("title", "what_happens", "why_not_settings"),
            },
            ["_meta"] = new JsonObject { ["anthropic/requiresUserInteraction"] = true },
            ["annotations"] = new JsonObject { ["title"] = "Report Problem", ["readOnlyHint"] = false, ["destructiveHint"] = false, ["openWorldHint"] = false },
        };

        yield return new JsonObject
        {
            ["name"] = "get_proposal_status",
            ["description"] = "Reports whether a proposal made earlier is pending, accepted or declined.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("id"),
            },
            ["annotations"] = new JsonObject { ["title"] = "Get Proposal Status", ["readOnlyHint"] = true, ["openWorldHint"] = false },
        };
    }

    /// <inheritdoc />
    public bool Handles(string toolName) => ReadTools.Contains(toolName) || WriteTools.Contains(toolName);

    /// <inheritdoc />
    public McpToolResult Call(string toolName, JsonObject arguments, McpAccess access) => toolName switch
    {
        "get_configuration" => GetConfiguration(arguments),
        "add_glossary_terms" => AddGlossaryTerms(arguments, access),
        "update_system_prompt" => UpdateSystemPrompt(arguments, access),
        "update_quick_instructions" => UpdateQuickInstructions(arguments, access),
        "report_problem" => ReportProblem(arguments, access),
        "get_proposal_status" => GetProposalStatus(arguments, access),
        _ => new McpToolResult("Unknown tool: " + toolName, true),
    };

    // ---- shared helpers ----------------------------------------------------------------------------------------

    private static string Norm(string? s) => (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static string EngineName(Engine e) => e switch { Engine.Local => "local", Engine.OpenAI => "openai", _ => "gemini" };

    private static string DefaultFor(string section) =>
        section == "dictation" ? Prompts.DefaultTranscriptionSystemPrompt : Prompts.DefaultPromptModeSystemPrompt;

    private string EffectivePrompt(string section)
    {
        var stored = ctx.Applier.CurrentSystemPrompt(section);
        return string.IsNullOrWhiteSpace(stored) ? DefaultFor(section) : stored;
    }

    private string TemplateText() => ctx.Settings.DictatePromptTemplate.Trim();

    private static string? StringArg(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private McpToolResult Done(string tool, string outcome, int n, JsonNode body)
    {
        AppLog.Info($"MCP: {tool} {outcome} n={n}");
        return new McpToolResult(McpToolContext.Json(body), false);
    }

    // ---- get_configuration -------------------------------------------------------------------------------------

    private McpToolResult GetConfiguration(JsonObject args)
    {
        var s = ctx.Settings;
        var llmId = OfflineCatalog.LlmById(s.OfflineLlmId).Id;
        var include = new HashSet<string>();
        if (args["include"] is JsonArray inc)
            foreach (var i in inc) if (i is JsonValue v && v.TryGetValue<string>(out var t)) include.Add(t);

        JsonObject PromptInfo(string section, string usedBy)
        {
            var stored = ctx.Applier.CurrentSystemPrompt(section);
            return new JsonObject
            {
                ["customized"] = !string.IsNullOrWhiteSpace(stored),
                ["length"] = EffectivePrompt(section).Length,
                ["default_length"] = DefaultFor(section).Length,
                ["used_by"] = usedBy,
            };
        }

        string Key(string hotkey) => HotkeyRules.TryNormalize(hotkey, out var n) ? n : hotkey;

        var quick = new JsonArray();
        foreach (var q in s.QuickActions) quick.Add(q);

        var o = new JsonObject
        {
            ["app_version"] = ctx.Host.AppVersion,
            ["platform"] = "windows",
            ["engines"] = new JsonObject { ["dictation"] = EngineName(s.SpeechEngine), ["dictate_prompt"] = EngineName(s.TextEngine) },
            ["offline_mode"] = s.OfflineMode,
            ["models"] = new JsonObject
            {
                ["dictation_gemini"] = s.TranscriptionModel,
                ["dictation_openai"] = s.OpenAiTranscriptionModel,
                ["dictate_prompt_gemini"] = s.PromptModel,
                ["dictate_prompt_openai"] = s.OpenAiPromptModel,
                ["offline_llm"] = llmId,
            },
            // Whether a key exists, never the key.
            ["api_keys_set"] = new JsonObject { ["gemini"] = !string.IsNullOrWhiteSpace(s.ApiKey), ["openai"] = !string.IsNullOrWhiteSpace(s.OpenAiKey) },
            ["offline_models"] = new JsonObject { ["speech_installed"] = ctx.Host.SpeechModelInstalled, ["llm_installed"] = ctx.Host.LocalLlmInstalled(llmId) },
            ["hotkeys"] = new JsonObject
            {
                ["dictate"] = Key(s.ActiveDictateHotkey),
                ["dictate_prompt"] = Key(s.ActivePromptHotkey),
                ["add_to_glossary"] = Key(s.ActiveGlossaryHotkey),
            },
            ["system_prompts"] = new JsonObject
            {
                ["dictation"] = PromptInfo("dictation", "Gemini speech recognition only (Parakeet and OpenAI take no instructions)"),
                ["dictate_prompt"] = PromptInfo("dictate_prompt", "Dictate Prompt on every engine, including offline"),
            },
            ["glossary"] = new JsonObject
            {
                ["count"] = s.Glossary.Count,
                ["characters"] = s.Glossary.Sum(l => l.Length),
                ["note"] = "One term per line. Term (not \"wrong\") marks misheard forms. Used by every engine, also offline.",
            },
            ["quick_instructions"] = quick,
            // TODO: a template tool comes with templates PR #26; until then the template is read-only for AI apps.
            ["template"] = new JsonObject { ["customized"] = TemplateText().Length > 0, ["length"] = TemplateText().Length },
            ["ai_app_access"] = new JsonObject { ["level"] = McpConfig.LevelName(ctx.Config.AccessLevel), ["pending_proposals"] = ctx.Proposals.Pending.Count },
            ["settings"] = "call get_settings",
            ["tune_recipe"] = McpTuneRecipe.Text(null),
        };

        if (include.Contains("system_prompts"))
            o["system_prompt_texts"] = new JsonObject { ["dictation"] = EffectivePrompt("dictation"), ["dictate_prompt"] = EffectivePrompt("dictate_prompt") };
        if (include.Contains("glossary"))
        {
            var terms = new JsonArray();
            foreach (var l in s.Glossary) terms.Add(l);
            o["glossary_terms"] = terms;
        }
        if (include.Contains("template")) o["template_text"] = TemplateText();

        AppLog.Info("MCP: get_configuration read");
        return new McpToolResult(McpToolContext.Json(o), false);
    }

    // ---- add_glossary_terms ------------------------------------------------------------------------------------

    private static readonly char[] QuoteChars = { ' ', '\t', '"', '\'', '„', '“', '”', '‘', '’', '‚', '«', '»' };

    // One term or misheard form, cleaned; null + error when it cannot be a glossary entry.
    private static string? CleanTerm(string raw, string what, out string? error)
    {
        error = null;
        var t = raw.Trim(QuoteChars);
        t = Whitespace.Replace(t, " ");
        if (t.Length == 0) { error = $"{what} is empty."; return null; }
        if (t.Length > GlossaryEdit.MaxTermLength) { error = $"\"{Shorten(t)}\" is {t.Length} characters; a term may have at most {GlossaryEdit.MaxTermLength}."; return null; }
        if (!GlossaryPrompt.WithinWordCap(t)) { error = $"\"{Shorten(t)}\" has more than {GlossaryPrompt.MaxTermWords} words; add the name or term, not a phrase."; return null; }
        if (t.Contains(',')) { error = $"\"{Shorten(t)}\" contains a comma; send one term per item."; return null; }
        if (t.Contains('(') || t.Contains(')')) { error = $"\"{Shorten(t)}\" contains parentheses; write misheard forms as: Term (not \"wrong\")."; return null; }
        return t;
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";

    private McpToolResult AddGlossaryTerms(JsonObject args, McpAccess access)
    {
        const string tool = "add_glossary_terms";
        if (ctx.CheckWrite(tool, access) is { } blocked) return blocked;
        var (rationale, rerr) = ctx.Rationale(args, tool);
        if (rerr != null) return rerr;

        if (args["terms"] is not JsonArray arr || arr.Count == 0 || arr.Any(i => !(i is JsonValue v && v.TryGetValue<string>(out _))))
            return ctx.Reject(tool, "terms invalid", "terms must be a non-empty list of strings.");
        if (arr.Count > MaxTermsPerCall)
            return ctx.Reject(tool, "too many terms", $"terms has {arr.Count} items; the limit is {MaxTermsPerCall} per call.");

        var parsed = new List<(string Term, string Line, bool HasForms)>();
        foreach (var item in arr)
        {
            var raw = item!.GetValue<string>();
            if (raw.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                return ctx.Reject(tool, "line break", "A term contains a line break; send one term per item.");
            var term = CleanTerm(GlossaryPrompt.LineTermText(raw), "A term", out var err);
            if (term == null) return ctx.Reject(tool, "invalid term", err!);
            var forms = new List<string>();
            foreach (var f in GlossaryPrompt.RejectedSpellings(raw))
            {
                var form = CleanTerm(f, "A misheard form", out var ferr);
                if (form == null) return ctx.Reject(tool, "invalid form", ferr!);
                if (GlossaryPrompt.Fold(form) == GlossaryPrompt.Fold(term)) continue;
                if (forms.Any(x => GlossaryPrompt.Fold(x) == GlossaryPrompt.Fold(form))) continue;
                forms.Add(form);
            }
            var line = forms.Count == 0 ? term : $"{term} (not {string.Join(", ", forms.Select(f => $"\"{f}\""))})";
            parsed.Add((term, line, forms.Count > 0));
        }

        var previous = ctx.Settings.Glossary.ToList();
        var merged = previous.ToList();
        var index = new Dictionary<string, int>();
        for (var i = 0; i < merged.Count; i++) index.TryAdd(GlossaryPrompt.Fold(GlossaryPrompt.LineTermText(merged[i])), i);

        var added = new List<string>();
        foreach (var (term, line, hasForms) in parsed)
        {
            var key = GlossaryPrompt.Fold(term);
            if (index.TryGetValue(key, out var at))
            {
                // Adds information without removing any: a bare line may become the annotated one.
                if (hasForms && GlossaryPrompt.RejectedSpellings(merged[at]).Count == 0)
                {
                    merged[at] = line;
                    added.Add(term);
                }
                continue;
            }
            index[key] = merged.Count;
            merged.Add(line);
            added.Add(term);
        }

        if (added.Count == 0)
            return Done(tool, "no_change", 0, new JsonObject { ["status"] = "no_change", ["message"] = "All of these terms are already in the glossary." });

        var used = previous.Sum(l => l.Length);
        var total = merged.Sum(l => l.Length);
        if (total > GlossaryCharBudget)
            return ctx.Reject(tool, "budget", $"The glossary has a budget of {GlossaryCharBudget} characters and {Math.Max(0, GlossaryCharBudget - used)} are left; these terms need {total - used}. Add fewer or shorter terms.");

        try
        {
            if (ctx.Config.AccessLevel == McpAccessLevel.Propose)
            {
                var p = ctx.Proposals.Create(McpProposalKind.Glossary, "Begriffe fürs Glossar", rationale!,
                    previousContent: string.Join("\n", previous), proposedContent: string.Join("\n", merged));
                AppLog.Info($"MCP: {tool} proposed n={added.Count}");
                return ctx.Pending(p);
            }
            var title = McpConfigApplier.GlossaryTitle(added.Count);
            var id = ctx.Applier.ApplyGlossary(merged, title, rationale!, false);
            var addedJson = new JsonArray();
            foreach (var a in added) addedJson.Add(a);
            return Done(tool, "applied", added.Count, new JsonObject
            {
                ["status"] = "applied", ["change_id"] = id, ["added"] = addedJson, ["glossary_count"] = merged.Count,
            });
        }
        catch (McpConfigException ex)
        {
            return ctx.Reject(tool, "failed", ex.Message);
        }
    }

    // ---- update_system_prompt ----------------------------------------------------------------------------------

    private McpToolResult UpdateSystemPrompt(JsonObject args, McpAccess access)
    {
        const string tool = "update_system_prompt";
        if (ctx.CheckWrite(tool, access) is { } blocked) return blocked;
        var (rationale, rerr) = ctx.Rationale(args, tool);
        if (rerr != null) return rerr;

        var section = StringArg(args, "section");
        if (section is not ("dictation" or "dictate_prompt"))
            return ctx.Reject(tool, "unknown section", "section must be \"dictation\" or \"dictate_prompt\".");
        var content = Norm(StringArg(args, "content"));
        if (content.Length == 0) return ctx.Reject(tool, "empty content", "content must not be empty.");
        if (content.Length > MaxPromptCharacters)
            return ctx.Reject(tool, "content too long", $"content has {content.Length} characters; the limit is {MaxPromptCharacters}.");

        if (Norm(EffectivePrompt(section)) == content)
            return Done(tool, "no_change", 0, new JsonObject { ["status"] = "no_change", ["message"] = "That is already the current text." });

        try
        {
            // Always a proposal, at every access level: it is applied to everything the user dictates
            // (the Mac app also always asks for the dictation instructions).
            var title = section == "dictation" ? "Neue Anweisungen fürs Diktieren" : "Neue Anweisungen für „Text bearbeiten“";
            var p = ctx.Proposals.Create(McpProposalKind.SystemPrompt, title, rationale!, section,
                previousContent: ctx.Applier.CurrentSystemPrompt(section), proposedContent: content);
            AppLog.Info($"MCP: {tool} proposed length={content.Length}");
            return ctx.Pending(p);
        }
        catch (McpConfigException ex)
        {
            return ctx.Reject(tool, "failed", ex.Message);
        }
    }

    // ---- update_quick_instructions -----------------------------------------------------------------------------

    private McpToolResult UpdateQuickInstructions(JsonObject args, McpAccess access)
    {
        const string tool = "update_quick_instructions";
        if (ctx.CheckWrite(tool, access) is { } blocked) return blocked;
        var (rationale, rerr) = ctx.Rationale(args, tool);
        if (rerr != null) return rerr;

        if (args["instructions"] is not JsonArray arr || arr.Any(i => !(i is JsonValue v && v.TryGetValue<string>(out _))))
            return ctx.Reject(tool, "instructions invalid", "instructions must be a list of strings.");
        if (arr.Count > QuickActionList.Max)
            return ctx.Reject(tool, "too many", $"instructions has {arr.Count} items; the limit is {QuickActionList.Max}.");
        var items = new List<string>();
        foreach (var i in arr)
        {
            var t = Whitespace.Replace(i!.GetValue<string>().Trim(), " ");
            if (t.Length > QuickActionList.MaxLength)
                return ctx.Reject(tool, "too long", $"An instruction has {t.Length} characters; the limit is {QuickActionList.MaxLength}.");
            items.Add(t);
        }
        var cleaned = QuickActionList.Clean(items);
        var current = ctx.Settings.QuickActions;
        if (cleaned.SequenceEqual(current))
            return Done(tool, "no_change", 0, new JsonObject { ["status"] = "no_change", ["message"] = "That is already the current list." });

        try
        {
            if (ctx.Config.AccessLevel == McpAccessLevel.Propose)
            {
                var p = ctx.Proposals.Create(McpProposalKind.QuickInstructions, "Neue Schnellanweisungen", rationale!,
                    previousContent: string.Join("\n", current), proposedContent: string.Join("\n", cleaned));
                AppLog.Info($"MCP: {tool} proposed n={cleaned.Count}");
                return ctx.Pending(p);
            }
            var id = ctx.Applier.ApplyQuickInstructions(cleaned, rationale!, false);
            var list = new JsonArray();
            foreach (var c in cleaned) list.Add(c);
            return Done(tool, "applied", cleaned.Count, new JsonObject { ["status"] = "applied", ["change_id"] = id, ["quick_instructions"] = list });
        }
        catch (McpConfigException ex)
        {
            return ctx.Reject(tool, "failed", ex.Message);
        }
    }

    // ---- report_problem ----------------------------------------------------------------------------------------

    private (string? Value, McpToolResult? Error) Bounded(JsonObject args, string key, int limit, bool required)
    {
        var raw = args[key];
        if (raw == null) return required ? (null, ctx.Reject("report_problem", key + " missing", $"{key} is required.")) : (null, null);
        if (raw is not JsonValue v || !v.TryGetValue<string>(out var s))
            return (null, ctx.Reject("report_problem", key + " not a string", $"{key} must be a string."));
        var t = s.Trim();
        if (t.Length == 0) return required ? (null, ctx.Reject("report_problem", key + " empty", $"{key} must not be empty.")) : (null, null);
        if (t.Length > limit) return (null, ctx.Reject("report_problem", key + " too long", $"{key} has {t.Length} characters; the limit is {limit}."));
        return (t, null);
    }

    private McpToolResult ReportProblem(JsonObject args, McpAccess access)
    {
        const string tool = "report_problem";
        if (ctx.CheckWrite(tool, access) is { } blocked) return blocked;

        var (title, e1) = Bounded(args, "title", 120, true);
        if (e1 != null) return e1;
        var (what, e2) = Bounded(args, "what_happens", 1000, true);
        if (e2 != null) return e2;
        var (why, e3) = Bounded(args, "why_not_settings", 500, true);
        if (e3 != null) return e3;
        var (cause, e4) = Bounded(args, "suspected_cause", 1000, false);
        if (e4 != null) return e4;

        var examples = new List<string>();
        if (args["examples"] != null)
        {
            if (args["examples"] is not JsonArray list || list.Any(i => !(i is JsonValue v && v.TryGetValue<string>(out _))))
                return ctx.Reject(tool, "examples not strings", "examples must be a list of strings.");
            if (list.Count > 3) return ctx.Reject(tool, "too many examples", $"examples has {list.Count} items; the limit is 3.");
            foreach (var i in list)
            {
                var t = i!.GetValue<string>().Trim();
                if (t.Length > 500) return ctx.Reject(tool, "example too long", $"An example has {t.Length} characters; the limit is 500.");
                if (t.Length > 0) examples.Add(t);
            }
        }

        string path;
        try
        {
            path = McpProblemReport.Write(ctx.ReportDirectory, title!, what!, examples, cause, why!, ctx.Settings, ctx.Host.AppVersion, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.Reject(tool, "write failed", "The report could not be saved: " + ex.Message);
        }
        try { ctx.Host.OpenProblemReport(path); }
        catch (Exception ex) { AppLog.Error("MCP: opening the problem report failed", ex); }
        AppLog.Info("MCP: report_problem saved");
        return new McpToolResult($"Saved as a problem report and opened for the user in WhisperShortcut's folder (file {Path.GetFileName(path)}). Nothing is sent; the user decides whether to report it.", false);
    }

    // ---- get_proposal_status -----------------------------------------------------------------------------------

    private McpToolResult GetProposalStatus(JsonObject args, McpAccess access)
    {
        const string tool = "get_proposal_status";
        // Needs the write token only; no access level above read and no rationale.
        if (access != McpAccess.Write) return ctx.CheckWrite(tool, access)!;
        var p = StringArg(args, "id") is { } id ? ctx.Proposals.Find(id) : null;
        if (p == null) return ctx.Reject(tool, "unknown id", "No proposal with that id.");
        return Done(tool, "read", 1, new JsonObject
        {
            ["id"] = p.Id,
            ["status"] = p.Status.ToString().ToLowerInvariant(),
            ["edited"] = p.Edited,
        });
    }

    // ---- resources ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public IEnumerable<JsonObject> Resources()
    {
        JsonObject R(string uri, string name, string description) =>
            new() { ["uri"] = uri, ["name"] = name, ["description"] = description, ["mimeType"] = "text/markdown" };
        yield return R(DictationUri, "Dictation instructions", "Effective text of the Dictation instructions (used only by Gemini speech recognition).");
        yield return R(DictatePromptUri, "Dictate Prompt instructions", "Effective text of the Dictate Prompt instructions (every engine).");
        yield return R(GlossaryUri, "Glossary", "The glossary, one term per line.");
        yield return R(TemplateUri, "Template", "The template for new texts in Dictate Prompt.");
    }

    /// <inheritdoc />
    public string? ReadResource(string uri) => uri switch
    {
        DictationUri => EffectivePrompt("dictation"),
        DictatePromptUri => EffectivePrompt("dictate_prompt"),
        GlossaryUri => string.Join("\n", ctx.Settings.Glossary),
        TemplateUri => TemplateText(),
        _ => null,
    };
}
