using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut;

/// <summary>
/// One change an AI app suggested, waiting for „Übernehmen“: why, what is there now, what it would be.
/// Texts (instructions, quick instructions) can be edited before taking them. Not modal and on top, so it
/// shows up next to the chat with Claude without blocking the app; „Später“ keeps it under Settings.
/// </summary>
internal sealed class McpApprovalDialog : Form
{
    private readonly McpService _service;
    private readonly McpProposal _proposal;
    private readonly TextBox? _editor;
    private readonly ThemedLabel _error;

    public McpApprovalDialog(McpService service, McpProposal proposal, string clientName)
    {
        _service = service;
        _proposal = proposal;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "WhisperShortcut – Vorschlag";
        Font = Theme.Body;
        Icon = TrayIcons.App;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, proposal.Kind is McpProposalKind.Settings ? 380 : 560);
        MinimumSize = new Size(520, 340);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(24, 20, 24, 16) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control c, bool fill = false)
        {
            root.Controls.Add(c);
            root.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        }

        Add(new ThemedLabel { Text = $"{clientName} schlägt vor: {proposal.Title}", Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, MaximumSize = new Size(570, 0), Margin = new Padding(0, 0, 0, 6) });
        Add(new ThemedLabel { Text = "Begründung: " + proposal.Rationale, AutoSize = true, MaximumSize = new Size(570, 0), Tone = Tone.Secondary, Margin = new Padding(0, 0, 0, 12) });

        switch (proposal.Kind)
        {
            case McpProposalKind.Settings:
                var list = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
                list.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                foreach (var item in proposal.SettingsChanges ?? new())
                {
                    list.Controls.Add(new ThemedLabel { Text = item.Title, Font = Theme.Strong, AutoSize = true, Margin = new Padding(0, 4, 16, 4) });
                    list.Controls.Add(new ThemedLabel { Text = $"{item.FromLabel}  →  {item.ToLabel}", AutoSize = true, Margin = new Padding(0, 4, 0, 4) });
                }
                Add(list, fill: true);
                break;

            case McpProposalKind.Glossary:
                // Only the new lines: the glossary itself can be long, and nothing in it is removed.
                var added = Added(proposal.PreviousContent, proposal.ProposedContent);
                Add(new ThemedLabel { Text = added.Count == 1 ? "Neu im Glossar:" : $"Neu im Glossar ({added.Count} Begriffe):", Font = Theme.Strong, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
                Add(Framed(Box(string.Join("\r\n", added), readOnly: true)), fill: true);
                Add(Hint("Was schon im Glossar steht, bleibt. Einzelne Begriffe lassen sich nach dem Übernehmen unter Einstellungen → Diktieren → Glossar löschen."));
                break;

            default:
                bool lines = proposal.Kind == McpProposalKind.QuickInstructions;
                var before = proposal.PreviousContent ?? "";
                if (proposal.Kind == McpProposalKind.SystemPrompt && before.Length == 0) before = "(Standardtext der App)";
                Add(new ThemedLabel { Text = "Bisher", Font = Theme.Strong, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
                var old = Framed(Box(before.ReplaceLineEndings("\r\n"), readOnly: true));
                old.Dock = DockStyle.Top;
                old.Height = 110;
                Add(old);
                Add(new ThemedLabel { Text = lines ? "Neu (eine pro Zeile, höchstens fünf; hier noch änderbar)" : "Neu (hier noch änderbar)", Font = Theme.Strong, AutoSize = true, Margin = new Padding(0, 10, 0, 4) });
                _editor = Box((proposal.ProposedContent ?? "").ReplaceLineEndings("\r\n"), readOnly: false);
                Add(Framed(_editor), fill: true);
                break;
        }

        _error = new ThemedLabel { AutoSize = true, MaximumSize = new Size(570, 0), Tone = Tone.Bad, Visible = false, Margin = new Padding(0, 6, 0, 0) };
        Add(_error);

        var accept = new FluentButton("Übernehmen", accent: true) { MinimumSize = new Size(120, 32) };
        var decline = new FluentButton("Ablehnen") { MinimumSize = new Size(112, 32), Margin = new Padding(8, 0, 0, 0) };
        var later = new FluentButton("Später") { MinimumSize = new Size(112, 32), Margin = new Padding(8, 0, 0, 0) };
        accept.Click += (_, _) => Accept();
        decline.Click += (_, _) =>
        {
            _service.Proposals.Decline(_proposal.Id);
            Core.Log.Info($"MCP: proposal {_proposal.Kind} declined");
            Close();
        };
        later.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.Add(accept);
        buttons.Controls.Add(decline);
        buttons.Controls.Add(later);
        Add(buttons);

        Controls.Add(root);
        CancelButton = later;
        ResumeLayout(true);

        Load += (_, _) => Theme.Apply(this);
        Shown += (_, _) => accept.Focus();
        // Superseded by a newer suggestion, or decided in another window: this one is stale.
        _service.Proposals.Changed += OnProposalsChanged;
        FormClosed += (_, _) => _service.Proposals.Changed -= OnProposalsChanged;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }

    private void OnProposalsChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (!IsDisposed && _service.Proposals.Find(_proposal.Id)?.Status != McpProposalStatus.Pending) Close();
        });
    }

    private void Accept()
    {
        if (_service.AcceptBlockedReason() is { } blocked)
        {
            ShowError(blocked);
            return;
        }
        string? edited = null;
        if (_editor != null)
        {
            edited = _editor.Text.ReplaceLineEndings("\n").Trim();
            if (_proposal.Kind == McpProposalKind.QuickInstructions)
            {
                var items = edited.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                if (items.Count > Core.QuickActionList.Max || items.Any(l => l.Length > Core.QuickActionList.MaxLength))
                {
                    ShowError($"Höchstens {Core.QuickActionList.Max} Schnellanweisungen mit je bis zu {Core.QuickActionList.MaxLength} Zeichen.");
                    return;
                }
                edited = string.Join("\n", items);
            }
            if (edited == (_proposal.ProposedContent ?? "").ReplaceLineEndings("\n").Trim()) edited = null;
        }
        try
        {
            _service.Proposals.Accept(_proposal.Id, edited);
            Core.Log.Info($"MCP: proposal {_proposal.Kind} accepted (edited {edited != null})");
            Close();
        }
        catch (McpConfigException e)
        {
            ShowError(e.Message);
        }
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.Visible = true;
    }

    private static TextBox Box(string text, bool readOnly) => new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, ReadOnly = readOnly, AcceptsReturn = !readOnly,
        Text = text,
    };

    private static FieldFrame Framed(TextBox box) => new(box, multiline: true) { Dock = DockStyle.Fill, Margin = new Padding(0) };

    private static ThemedLabel Hint(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(570, 0), Tone = Tone.Secondary, Font = Theme.Small, Margin = new Padding(0, 6, 0, 0),
    };

    /// <summary>Lines of <paramref name="proposed"/> that are not in <paramref name="previous"/>.</summary>
    private static List<string> Added(string? previous, string? proposed)
    {
        var old = new HashSet<string>((previous ?? "").Split('\n').Select(l => l.Trim()), StringComparer.Ordinal);
        return (proposed ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !old.Contains(l)).ToList();
    }
}
