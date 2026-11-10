using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut;

/// <summary>
/// Page „Claude &amp; KI-Apps“: the local MCP server that lets Claude set WhisperShortcut up. Kept in its own
/// file so the main settings file only gets the page hook. Everything here takes effect at once (like the
/// tray's offline switch); none of it goes through the window's own apply.
/// </summary>
internal sealed partial class SettingsForm
{
    public const string AiAppsPage = "Claude & KI-Apps";

    private McpService? _mcp;
    private ToggleSwitch _mcpSwitch = null!;
    private ThemedLabel _mcpStatus = null!;
    private OptionButton _mcpRead = null!, _mcpPropose = null!, _mcpApply = null!;
    private ThemedLabel _desktopState = null!, _codeState = null!;
    private FluentButton _desktopConnect = null!, _codeConnect = null!, _copySetup = null!, _newKeys = null!;
    private Control[] _needsServer = Array.Empty<Control>();
    private Card _pendingCard = null!;
    private ThemedLabel _pendingText = null!;
    private TableLayoutPanel _changesBlock = null!;
    private readonly List<Label> _changeWraps = new();
    private readonly System.Windows.Forms.Timer _mcpClock = new() { Interval = 30_000 };

    private void BuildAiAppsPage(McpService? mcp)
    {
        var page = NewPage(AiAppsPage, "Claude kann WhisperShortcut für dich einrichten: Namen und Fachwörter ins Glossar eintragen, "
            + "Anweisungen und Schnellanweisungen vorschlagen, Modelle und Tastenkürzel anpassen. Danach in Claude zum Beispiel "
            + "sagen: „Richte mein Diktieren in WhisperShortcut ein.“");
        _mcp = mcp;
        if (mcp == null) return;

        // --- on / off ---
        page.Controls.Add(Section("Verbindung", first: true));
        _mcpSwitch = new ToggleSwitch { Checked = mcp.Config.Enabled, AccessibleName = "Verbindung für KI-Apps" };
        var switchCard = new Card();
        switchCard.AddRow(SettingRow("KI-Apps auf diesem PC die Verbindung erlauben", "", _mcpSwitch, ToggleWidth, out _mcpStatus));
        var privacy = Block();
        privacy.Controls.Add(Wrap("Nur Apps auf diesem PC kommen heran, und nur mit dem Zugangsschlüssel. Was Claude hier liest (Glossar, "
            + "Anweisungen, Einstellungen), geht an Anthropic. Diktierte Texte, Aufnahmen und API-Schlüssel gibt WhisperShortcut nie heraus. "
            + "Im Offline-Modus ist die Verbindung pausiert.", 2 * RowPadding, small: true));
        switchCard.AddRow(privacy);
        page.Controls.Add(switchCard);
        _mcpSwitch.CheckedChanged += (_, _) => { if (_mcpSwitch.Checked != mcp.Config.Enabled) mcp.SetEnabled(_mcpSwitch.Checked); };

        // --- access level ---
        SectionIcons.TryAdd("KI-Apps dürfen", "\uE72E");
        page.Controls.Add(Section("KI-Apps dürfen"));
        _mcpRead = new OptionButton("Nur lesen");
        _mcpPropose = new OptionButton("Änderungen zur Freigabe vorschlagen");
        _mcpApply = new OptionButton("Änderungen direkt vornehmen");
        var levels = new[] { (_mcpRead, McpAccessLevel.Read), (_mcpPropose, McpAccessLevel.Propose), (_mcpApply, McpAccessLevel.Apply) };
        foreach (var (option, level) in levels)
        {
            option.Checked = mcp.Config.AccessLevel == level;
            // Each option sits in its own card row, so the automatic radio grouping does not apply.
            option.CheckedChanged += (_, _) =>
            {
                if (!option.Checked) return;
                foreach (var (other, _) in levels) if (other != option) other.Checked = false;
                mcp.SetAccessLevel(level);
            };
        }
        var levelCard = new Card();
        levelCard.AddRow(OptionRow(_mcpRead, "Claude sieht die Einrichtung und macht Vorschläge im Chat, ändert aber nichts."));
        levelCard.AddRow(OptionRow(_mcpPropose, "Jede Änderung erscheint in einem kleinen Fenster. Erst „Übernehmen“ ändert etwas."));
        levelCard.AddRow(OptionRow(_mcpApply, "Glossar, Schnellanweisungen und Einstellungen ändern sich sofort. Alles steht unten in der Liste "
            + "und lässt sich rückgängig machen."));
        page.Controls.Add(levelCard);
        page.Controls.Add(Wrap("Neue System-Prompts für Diktieren und Dictate Prompt und „Mit Windows starten“ warten immer auf deine Bestätigung.", 0));

        // --- connect ---
        page.Controls.Add(Section("Mit Claude verbinden"));
        _desktopConnect = new FluentButton("Verbinden") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Claude Desktop verbinden" };
        _codeConnect = new FluentButton("Verbinden") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Claude Code verbinden" };
        _copySetup = new FluentButton("Kopieren") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Einrichtung kopieren" };
        _newKeys = new FluentButton("Neuer Zugriffsschlüssel …") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Zugangsschlüssel erneuern" };
        var connectCard = new Card();
        connectCard.AddRow(SettingRow("Claude Desktop", "", _desktopConnect, ButtonWidth, out _desktopState));
        connectCard.AddRow(SettingRow("Claude Code", "", _codeConnect, ButtonWidth, out _codeState));
        connectCard.AddRow(SettingRow("Andere App oder von Hand", "Adresse, Zugangsschlüssel und die Einträge für Claude Desktop und "
            + "Claude Code als Text in die Zwischenablage. Der Text enthält den Schlüssel: nicht weitergeben.", _copySetup, ButtonWidth, out _));
        connectCard.AddRow(SettingRow("Zugangsschlüssel erneuern", "Neue Schlüssel, die alten gelten nicht mehr. Danach jede App noch "
            + "einmal verbinden.", _newKeys, ButtonWidth, out _));
        page.Controls.Add(connectCard);
        _needsServer = new Control[] { _desktopConnect, _codeConnect, _copySetup };
        _desktopConnect.Click += (_, _) => ConnectDesktop();
        _codeConnect.Click += async (_, _) => await ConnectCodeAsync();
        _copySetup.Click += (_, _) => CopySetup();
        _newKeys.Click += (_, _) => RegenerateKeys();

        // --- pending proposals (only while there are some) ---
        var view = new FluentButton("Ansehen") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Wartende Vorschläge ansehen" };
        view.Click += (_, _) => mcp.ShowPendingProposals();
        _pendingCard = new Card { Margin = new Padding(0, 16, 0, 4) };
        _pendingCard.AddRow(SettingRow("Wartende Vorschläge", "", view, ButtonWidth, out _pendingText));
        page.Controls.Add(_pendingCard);

        // --- change log ---
        SectionIcons.TryAdd("Von KI-Apps geändert", "\uE81C");
        page.Controls.Add(Section("Von KI-Apps geändert"));
        _changesBlock = Block();
        var changesCard = new Card();
        changesCard.AddRow(_changesBlock);
        page.Controls.Add(changesCard);

        mcp.Changed += RefreshAiApps;
        // „vor 2 Min.“ keeps counting while the window is open.
        _mcpClock.Tick += (_, _) => RefreshConnectionStates();
        // Only while visible: the window built in advance would otherwise relay out its hidden pages every
        // minute, and show the „vor … Min.“ of when it was built once it opens.
        VisibleChanged += (_, _) =>
        {
            if (Visible) RefreshConnectionStates();
            _mcpClock.Enabled = Visible;
        };
        Disposed += (_, _) =>
        {
            mcp.Changed -= RefreshAiApps;
            _mcpClock.Dispose();
        };
        RefreshAiApps();
    }

    private void RefreshAiApps()
    {
        if (_mcp == null || IsDisposed) return;
        var (text, tone) = _mcp.Status;
        _mcpStatus.Text = text;
        _mcpStatus.Tone = tone;
        if (_mcpSwitch.Checked != _mcp.Config.Enabled) _mcpSwitch.Checked = _mcp.Config.Enabled;
        foreach (var c in _needsServer) c.Enabled = _mcp.Running;
        RefreshConnectionStates();

        int pending = _mcp.Proposals.Pending.Count;
        _pendingCard.Visible = pending > 0;
        _pendingText.Text = pending == 1 ? "Ein Vorschlag wartet auf „Übernehmen“ oder „Ablehnen“."
            : $"{pending} Vorschläge warten auf „Übernehmen“ oder „Ablehnen“.";
        RefreshChanges();
    }

    private void RefreshConnectionStates()
    {
        if (_mcp == null || IsDisposed) return;
        _desktopState.Text = ClientState("claude-ai", "Braucht Node.js (nodejs.org). WhisperShortcut trägt sich in Claudes Einstellungen ein; "
            + "danach Claude neu starten.");
        _codeState.Text = ClientState("claude-code", "Trägt WhisperShortcut für alle Projekte ein (claude mcp add).");
    }

    /// <summary>„Verbunden · darf vorschlagen · vor 3 Min.“, a warning when it holds the read-only key, or the hint before connecting.</summary>
    private string ClientState(string prefix, string notYet)
    {
        var client = _mcp!.Config.ClientMatching(prefix);
        if (client == null) return "Noch nicht verbunden. " + notYet;
        var when = Ago(client.LastConnectedUtc);
        if (client.Access == McpAccess.Read && _mcp.Config.AccessLevel != McpAccessLevel.Read)
            return $"Verbunden, aber nur mit dem Lese-Schlüssel ({when}). Noch einmal „Verbinden“, damit Claude Änderungen vorschlagen kann.";
        return $"✓ Verbunden, {(client.Access == McpAccess.Write ? "Lesen und Ändern" : "nur Lesen")} · {when}";
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.FromMinutes(1)) return "gerade eben";
        if (span < TimeSpan.FromHours(1)) return $"vor {(int)span.TotalMinutes} Min.";
        if (span < TimeSpan.FromDays(1)) return $"vor {(int)span.TotalHours} Std.";
        return utc.ToLocalTime().ToString("dd.MM.yyyy");
    }

    private void RefreshChanges()
    {
        _changesBlock.SuspendLayout();
        foreach (Control c in _changesBlock.Controls.Cast<Control>().ToList()) c.Dispose();
        _changesBlock.Controls.Clear();
        _changesBlock.RowStyles.Clear();
        _wraps.RemoveAll(w => _changeWraps.Contains(w.Label));
        _changeWraps.Clear();

        var changes = _mcp!.ChangeLog.Changes.Take(10).ToList();
        if (changes.Count == 0)
        {
            var none = new ThemedLabel { Text = "Noch keine. Was Claude direkt ändert oder was du übernimmst, steht hier mit „Rückgängig“.", AutoSize = true, Tone = Tone.Secondary };
            Track(none, 2 * RowPadding);
            _changesBlock.Controls.Add(none);
        }
        foreach (var change in changes)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var title = new ThemedLabel { Text = change.Title, AutoSize = true, Margin = new Padding(0) };
            var detail = new ThemedLabel
            {
                Text = $"{change.AppliedUtc.ToLocalTime():dd.MM. HH:mm}{(change.ViaProposal ? " · von dir übernommen" : "")} · {change.Rationale}",
                AutoSize = true, Tone = Tone.Secondary, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0),
            };
            Track(title, 2 * RowPadding + 140);
            Track(detail, 2 * RowPadding + 140);
            text.Controls.Add(title);
            text.Controls.Add(detail);
            row.Controls.Add(text, 0, 0);
            Control right;
            if (change.UndoneUtc != null)
                right = new ThemedLabel { Text = "rückgängig gemacht", AutoSize = true, Tone = Tone.Secondary, Font = Theme.Small };
            else
            {
                var undo = new LinkLabel { Text = "Rückgängig", AutoSize = true, AccessibleName = "Rückgängig: " + change.Title };
                var id = change.Id;
                undo.LinkClicked += (_, _) => UndoChange(id);
                right = undo;
            }
            right.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            right.Margin = new Padding(RowGap, 2, 0, 0);
            row.Controls.Add(right, 1, 0);
            _changesBlock.Controls.Add(row);
        }
        Theme.Apply(_changesBlock);
        _changesBlock.ResumeLayout(true);
        FitHints();
    }

    private void Track(Label label, int inset)
    {
        Fit(label, inset);
        _changeWraps.Add(label);
    }

    private void UndoChange(string id)
    {
        var result = _mcp!.Undo(id);
        Core.Log.Info($"MCP: undo {result.Outcome}");
        if (result.Outcome is McpUndoOutcome.Undone) return; // the list shows it
        MessageBox.Show(this, result.Message, "Rückgängig", MessageBoxButtons.OK,
            result.Outcome == McpUndoOutcome.UndonePartial ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void ConnectDesktop()
    {
        try { MessageBox.Show(this, _mcp!.ConnectClaudeDesktop(), "Claude Desktop", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        catch (Exception e) when (e is McpConfigException or IOException or UnauthorizedAccessException)
        {
            Core.Log.Error($"MCP: Claude Desktop config failed: {e.GetType().Name}");
            Warn("Claudes Einstellungen konnten nicht geschrieben werden: " + e.Message + "\n\nAlternativ „Kopieren“ und den Eintrag von Hand einfügen.");
        }
    }

    private async Task ConnectCodeAsync()
    {
        _codeConnect.Enabled = false;
        _codeState.Text = "Wird eingetragen …";
        try
        {
            var message = await _mcp!.ConnectClaudeCodeAsync();
            if (!IsDisposed) MessageBox.Show(this, message, "Claude Code", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is McpConfigException or IOException or UnauthorizedAccessException)
        {
            Core.Log.Error($"MCP: Claude Code config failed: {e.GetType().Name}");
            if (!IsDisposed) Warn("Claude Code konnte nicht eingerichtet werden: " + e.Message + "\n\nAlternativ „Kopieren“ und den Eintrag von Hand einfügen.");
        }
        if (IsDisposed) return;
        _codeConnect.Enabled = _mcp!.Running;
        RefreshConnectionStates();
    }

    private void CopySetup()
    {
        try
        {
            Clipboard.SetText(_mcp!.ManualSetup());
            MessageBox.Show(this, "Die Einrichtung liegt in der Zwischenablage. Sie enthält den Zugangsschlüssel: nur in die eigene KI-App "
                + "einfügen, nicht in eine Mail oder einen Chat mit anderen.", "Kopiert", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            Core.Log.Error($"MCP: copying the setup failed: {e.GetType().Name}");
            Warn("Die Zwischenablage ist gerade belegt. Bitte noch einmal versuchen.");
        }
    }

    private void RegenerateKeys()
    {
        if (ChoiceDialog.Ask(this, "Zugangsschlüssel erneuern?", "Die verbundenen Apps kommen danach nicht mehr herein, bis sie hier "
            + "noch einmal verbunden werden.", new[] { "Erneuern", "Abbrechen" }, danger: true) != 0) return;
        _mcp!.RegenerateKeys();
    }
}
