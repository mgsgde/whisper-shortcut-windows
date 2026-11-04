using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisperShortcut.Core.Mcp;

/// <summary>What a proposal would change.</summary>
public enum McpProposalKind
{
    /// <summary>Glossary terms.</summary>
    Glossary,
    /// <summary>A system prompt section.</summary>
    SystemPrompt,
    /// <summary>The quick instruction list.</summary>
    QuickInstructions,
    /// <summary>Catalog settings.</summary>
    Settings,
}

/// <summary>Where a proposal stands.</summary>
public enum McpProposalStatus
{
    /// <summary>Waiting for the user.</summary>
    Pending,
    /// <summary>Applied.</summary>
    Accepted,
    /// <summary>Rejected by the user or replaced by a newer one.</summary>
    Declined,
}

/// <summary>A change an AI app suggested; nothing happens until the user accepts it.</summary>
public sealed class McpProposal
{
    /// <summary>Unique id (the AI app polls it with get_proposal_status).</summary>
    public string Id { get; init; } = "";
    /// <summary>Creation time.</summary>
    public DateTime CreatedUtc { get; init; }
    /// <summary>What it changes.</summary>
    public McpProposalKind Kind { get; init; }
    /// <summary>German heading for the approval dialog.</summary>
    public string Title { get; init; } = "";
    /// <summary>"dictation" / "dictate_prompt" for <see cref="McpProposalKind.SystemPrompt"/>.</summary>
    public string? Section { get; init; }
    /// <summary>Content when the proposal was made (to detect later manual changes).</summary>
    public string? PreviousContent { get; init; }
    /// <summary>The suggested content; replaced by the user's edit.</summary>
    public string? ProposedContent { get; set; }
    /// <summary>Why the AI app says it helps.</summary>
    public string Rationale { get; init; } = "";
    /// <summary>Current status.</summary>
    public McpProposalStatus Status { get; set; }
    /// <summary>True when the user edited the text before accepting.</summary>
    public bool Edited { get; set; }
    /// <summary>The items of a settings proposal.</summary>
    public List<McpSettingsChangeItem>? SettingsChanges { get; init; }
}

/// <summary>Pending and resolved proposals, persisted as JSONL. Thread-safe.</summary>
public sealed class McpProposalStore
{
    /// <summary>Most proposals waiting at once.</summary>
    public const int MaxPending = 20;
    /// <summary>Resolved proposals kept for get_proposal_status.</summary>
    public const int MaxResolved = 100;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<McpProposal> _all; // oldest first

    /// <summary>Default location: mcp-proposals.jsonl in <see cref="AppData.SettingsFolder"/>.</summary>
    public static string DefaultPath => Path.Combine(AppData.SettingsFolder, "mcp-proposals.jsonl");

    /// <summary>Loads the file; unreadable lines are skipped.</summary>
    public McpProposalStore(string path)
    {
        _path = path;
        _all = McpJsonl.Load<McpProposal>(path, McpChangeLog.Json, p => !string.IsNullOrEmpty(p.Id));
    }

    /// <summary>Set after construction (the applier needs the catalog, which needs the settings).</summary>
    public McpConfigApplier? Applier { get; set; }

    /// <summary>Raised after any change (on the calling thread).</summary>
    public event Action? Changed;

    /// <summary>Proposals waiting for the user, newest first.</summary>
    public IReadOnlyList<McpProposal> Pending
    {
        get { lock (_gate) return Enumerable.Reverse(_all).Where(p => p.Status == McpProposalStatus.Pending).ToList(); }
    }

    private static bool SameTarget(McpProposal p, McpProposalKind kind, string? section) =>
        p.Kind == kind && (kind != McpProposalKind.SystemPrompt || p.Section == section);

    /// <summary>
    /// A newer pending proposal for the same target (same kind, and same section for system prompts) declines the older one.
    /// Throws <see cref="McpConfigException"/> past <see cref="MaxPending"/>.
    /// </summary>
    public McpProposal Create(McpProposalKind kind, string title, string rationale, string? section = null, string? previousContent = null, string? proposedContent = null, List<McpSettingsChangeItem>? settingsChanges = null)
    {
        McpProposal p;
        lock (_gate)
        {
            var superseded = _all.Where(x => x.Status == McpProposalStatus.Pending && SameTarget(x, kind, section)).ToList();
            var pending = _all.Count(x => x.Status == McpProposalStatus.Pending);
            if (pending - superseded.Count >= MaxPending)
                throw new McpConfigException("Too many changes are waiting for approval in WhisperShortcut. Ask the user to review them first.");
            foreach (var old in superseded) old.Status = McpProposalStatus.Declined;
            p = new McpProposal
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                Kind = kind,
                Title = title,
                Section = section,
                PreviousContent = previousContent,
                ProposedContent = proposedContent,
                Rationale = rationale,
                Status = McpProposalStatus.Pending,
                SettingsChanges = settingsChanges,
            };
            _all.Add(p);
            TrimResolved();
            Persist();
        }
        Log.Info($"MCP: proposal created kind={kind} section={section ?? "-"}");
        Changed?.Invoke();
        return p;
    }

    private void TrimResolved()
    {
        var resolved = _all.Where(x => x.Status != McpProposalStatus.Pending).ToList();
        for (var i = 0; i < resolved.Count - MaxResolved; i++) _all.Remove(resolved[i]);
    }

    /// <summary>The proposal with this id, or null.</summary>
    public McpProposal? Find(string id)
    {
        lock (_gate) return _all.FirstOrDefault(p => p.Id == id);
    }

    private static string Norm(string? s) => (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static List<string> Lines(string? text) =>
        Norm(text).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>
    /// Applies a pending proposal through <see cref="Applier"/> (viaProposal: true) and returns the change id.
    /// <paramref name="editedContent"/> (glossary, system prompt, quick instructions) replaces the proposed text.
    /// A failed apply leaves the proposal pending.
    /// </summary>
    public string Accept(string id, string? editedContent = null)
    {
        var applier = Applier ?? throw new McpConfigException("Die Einstellungen sind noch nicht bereit.");
        McpProposal p;
        lock (_gate)
        {
            p = _all.FirstOrDefault(x => x.Id == id) ?? throw new McpConfigException("Diesen Vorschlag gibt es nicht mehr.");
            if (p.Status != McpProposalStatus.Pending) throw new McpConfigException("Dieser Vorschlag wurde schon bearbeitet.");
        }

        const string Stale = "Das wurde inzwischen geändert. Bitte ablehnen und die KI-App noch einmal fragen.";
        var edited = editedContent != null && p.Kind != McpProposalKind.Settings && Norm(editedContent) != Norm(p.ProposedContent);
        var content = p.Kind == McpProposalKind.Settings ? p.ProposedContent : editedContent ?? p.ProposedContent;
        string changeId;
        switch (p.Kind)
        {
            case McpProposalKind.Glossary:
            {
                if (Norm(applier.CurrentGlossaryText()) != Norm(p.PreviousContent)) throw new McpConfigException(Stale);
                var before = Lines(p.PreviousContent);
                var lines = Lines(content);
                var added = lines.Count(l => !before.Contains(l));
                changeId = applier.ApplyGlossary(lines, McpConfigApplier.GlossaryTitle(added), p.Rationale, true);
                break;
            }
            case McpProposalKind.SystemPrompt:
            {
                var section = p.Section ?? "";
                if (Norm(applier.CurrentSystemPrompt(section)) != Norm(p.PreviousContent)) throw new McpConfigException(Stale);
                if (string.IsNullOrWhiteSpace(content)) throw new McpConfigException("Der Text darf nicht leer sein.");
                changeId = applier.ApplySystemPrompt(section, content, p.Rationale, true);
                break;
            }
            case McpProposalKind.QuickInstructions:
            {
                if (Norm(applier.CurrentQuickInstructionsText()) != Norm(p.PreviousContent)) throw new McpConfigException(Stale);
                changeId = applier.ApplyQuickInstructions(Lines(content), p.Rationale, true);
                break;
            }
            default:
            {
                var items = p.SettingsChanges ?? new();
                if (items.Count == 0) throw new McpConfigException("Dieser Vorschlag enthält keine Änderungen.");
                var batch = new List<(string, JsonNode?)>();
                foreach (var item in items)
                {
                    var setting = applier.Catalog.FirstOrDefault(s => s.Id == item.Id)
                        ?? throw new McpConfigException($"Die Einstellung „{item.Title}“ gibt es nicht mehr.");
                    JsonNode? value;
                    try { value = JsonNode.Parse(item.ToJson); }
                    catch (JsonException) { throw new McpConfigException($"Der Wert für „{item.Title}“ ist ungültig."); }
                    var problem = setting.Validate(value);
                    if (problem != null) throw new McpConfigException($"„{item.Title}“ ist jetzt nicht mehr möglich: {problem}");
                    batch.Add((item.Id, value));
                }
                var batchProblem = McpSettingsCatalog.ValidateBatch(applier.Catalog, batch, McpSettingsCatalog.FixedHotkeys(applier.Settings));
                if (batchProblem != null) throw new McpConfigException("Die Änderungen passen jetzt nicht mehr zusammen: " + batchProblem);
                changeId = applier.ApplySettings(items, p.Rationale, true);
                break;
            }
        }

        lock (_gate)
        {
            if (edited) { p.ProposedContent = content; p.Edited = true; }
            p.Status = McpProposalStatus.Accepted;
            TrimResolved();
            Persist();
        }
        Changed?.Invoke();
        return changeId;
    }

    /// <summary>Declines a pending proposal; unknown or already resolved ids do nothing.</summary>
    public void Decline(string id)
    {
        lock (_gate)
        {
            var p = _all.FirstOrDefault(x => x.Id == id);
            if (p == null || p.Status != McpProposalStatus.Pending) return;
            p.Status = McpProposalStatus.Declined;
            TrimResolved();
            Persist();
        }
        Changed?.Invoke();
    }

    private void Persist()
    {
        try { McpJsonl.Save(_path, _all, McpChangeLog.Json); }
        catch (Exception ex) { Log.Error("mcp-proposals.jsonl could not be saved", ex); }
    }
}
