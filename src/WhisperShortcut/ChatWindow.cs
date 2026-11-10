namespace WhisperShortcut;

/// <summary>
/// The chat window (Mac ChatWindowController / ChatView): earlier chats on the left, the conversation on the right, the input
/// at the bottom. Enter sends, Umschalt+Enter starts a new line, „/“ lists the commands (Mac slash menu). Answers stream in as
/// they are written; Gemini answers with Google Search show their sources. Opened and closed with its shortcut (Alt+6) or the
/// tray row „Chat“; like the Mac it closes when another app comes to the front, unless pinned with /pin.
/// Everything runs on the UI thread; stream pieces are posted back to it.
/// </summary>
internal sealed partial class ChatWindow : FastShowForm
{
    private readonly Core.Settings _settings;
    private readonly Core.ChatClient _client;
    private readonly OfflineEngine _offline;
    private readonly Action _toggleMeeting;
    private readonly Func<bool> _meetingActive;
    private readonly Action _openSettings;
    private readonly Action _save;
    private readonly Core.ChatSessionStore _store = new(Core.ChatSessionStore.DefaultRoot);

    private readonly ListBox _sessions = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46,
        IntegralHeight = false, AccessibleName = "Frühere Chats",
    };
    private readonly RichTextBox _transcript = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = true, ScrollBars = RichTextBoxScrollBars.Vertical,
        AccessibleName = "Chatverlauf", Margin = new Padding(0), ShortcutsEnabled = true, HideSelection = false,
    };
    private readonly TextBox _input = new()
    {
        Multiline = true, AcceptsReturn = false, WordWrap = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "Nachricht an den Chat",
        PlaceholderText = "Nachricht schreiben … (/ für Befehle)",
    };
    private readonly ListBox _commands = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, Visible = false, AccessibleName = "Befehle",
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 26,
    };
    private readonly LinkLabel _model = new() { AutoSize = true, AccessibleName = "Modell wechseln", Margin = new Padding(0, 4, 12, 4) };
    private readonly ThemedLabel _pin = new() { AutoSize = true, Tone = Tone.Secondary, Font = Theme.Small, Margin = new Padding(0, 6, 0, 4) };
    private readonly ThemedLabel _status = new() { AutoSize = true, Tone = Tone.Secondary, Font = Theme.Small, Margin = new Padding(0, 4, 0, 0) };
    private readonly FluentButton _send = new("Senden", accent: true) { MinimumSize = new Size(96, 32), AccessibleName = "Senden" };
    private readonly ContextMenuStrip _modelMenu = new();
    private readonly ContextMenuStrip _transcriptMenu = new();
    private readonly ContextMenuStrip _sessionMenu = new();

    private Core.ChatSession _session = Core.ChatSession.New(DateTimeOffset.Now);
    private CancellationTokenSource? _cts;
    // Engine of the running answer (null while idle).
    private Core.Engine? _runningEngine;
    private bool _pinned;
    private bool _holdOpen; // a dialog of ours is in front: that is no "click outside"
    private string _defaultStatus = "";

    /// <param name="toggleMeeting">/meeting: starts or stops the Live-Meeting.</param>
    /// <param name="save">Writes the settings after /model, /gemini, /gpt.</param>
    public ChatWindow(Core.Settings settings, Core.ChatClient client, OfflineEngine offline, Action toggleMeeting, Func<bool> meetingActive,
        Action openSettings, Action save)
    {
        _settings = settings;
        _client = client;
        _offline = offline;
        _toggleMeeting = toggleMeeting;
        _meetingActive = meetingActive;
        _openSettings = openSettings;
        _save = save;

        Text = "Chat";
        Icon = TrayIcons.App;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 720);
        MinimumSize = new Size(640, 420);
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        // --- sidebar: new chat and the earlier ones (Mac ChatSidebar) ---
        var newChat = new FluentButton("Neuer Chat") { Dock = DockStyle.Top, Height = 34, AccessibleName = "Neuer Chat" };
        newChat.Click += (_, _) => NewChat();
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 240, Padding = new Padding(12, 12, 8, 12) };
        var listHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
        listHolder.Controls.Add(_sessions);
        sidebar.Controls.Add(listHolder);
        sidebar.Controls.Add(newChat);
        _sessions.DrawItem += DrawSession;
        _sessions.SelectedIndexChanged += (_, _) =>
        {
            if (_sessions.SelectedItem is Core.ChatSession s && s.Id != _session.Id && _cts == null) Open(s);
        };
        _sessionMenu.Items.Add("Chat löschen", null, (_, _) => DeleteSelected());
        _sessions.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            int i = _sessions.IndexFromPoint(e.Location);
            if (i < 0) return;
            _sessions.SelectedIndex = i;
            Theme.Menu(_sessionMenu);
            _sessionMenu.Show(_sessions, e.Location);
        };

        // --- main column ---
        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 6) };
        header.Controls.Add(_model);
        header.Controls.Add(_pin);
        AddHeaderExtras(header); // ChatWindow.Meeting.cs: thinking depth, meeting chat
        _model.LinkClicked += (_, _) => ShowModelMenu();

        _transcriptMenu.Items.Add("Kopieren", null, (_, _) => { if (_transcript.SelectionLength > 0) _transcript.Copy(); });
        _transcriptMenu.Items.Add("Letzte Antwort kopieren", null, (_, _) => CopyLastAnswer(blockOnly: false));
        _transcriptMenu.Items.Add("Letzten Textblock kopieren", null, (_, _) => CopyLastAnswer(blockOnly: true));
        _transcript.ContextMenuStrip = _transcriptMenu;
        _transcript.LinkClicked += (_, e) => OpenLink(e.LinkText ?? "");

        var inputFrame = new FieldFrame(_input, multiline: true) { Dock = DockStyle.Fill, Height = 76 };
        var inputRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputRow.Controls.Add(inputFrame, 0, 0);
        _send.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _send.Margin = new Padding(8, 0, 0, 0);
        inputRow.Controls.Add(_send, 1, 0);
        _send.Click += (_, _) => Submit();

        var commandsHolder = new Panel { Dock = DockStyle.Top, Height = 0, Padding = new Padding(1) };
        commandsHolder.Controls.Add(_commands);
        _commands.DrawItem += DrawCommand;
        _commands.MouseClick += (_, _) => AcceptCommand();

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12, 12, 16, 12) };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.Controls.Add(header, 0, 0);
        var transcriptFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 6, 10), Margin = new Padding(0) };
        transcriptFrame.Controls.Add(_transcript);
        main.Controls.Add(transcriptFrame, 0, 1);
        main.Controls.Add(commandsHolder, 0, 2);
        main.Controls.Add(BuildComposerExtras(), 0, 3); // ChatWindow.Attachments.cs / .Meeting.cs: attachment chips, meeting buttons
        main.Controls.Add(inputRow, 0, 4);
        main.Controls.Add(_status, 0, 5);
        Controls.Add(main);
        Controls.Add(sidebar);

        _input.KeyDown += OnInputKey;
        Activated += (_, _) => OnBroughtToFront(); // Mac: the commands folder is re-read whenever the window comes to the front
        _input.TextChanged += (_, _) => UpdateCommands();
        Deactivate += (_, _) =>
        {
            // Mac chatCloseOnFocusLoss: another app in front closes the chat; /pin keeps it, our own dialogs do not count.
            if (_settings.ChatCloseOnFocusLoss && !_pinned && !_holdOpen && Visible) BeginInvoke(() => { if (!ContainsFocus) Hide(); });
        };

        _transcript.BackColor = _sessions.BackColor = _commands.BackColor = Theme.Card;
        _transcript.ForeColor = _sessions.ForeColor = _commands.ForeColor = Theme.Text;
        transcriptFrame.BackColor = Theme.Card;
        sidebar.BackColor = listHolder.BackColor = Theme.Window;
        _sessions.BackColor = Theme.Window;
        Theme.Apply(this);
        ReloadSessions();
        UpdateHeader();
        Render();
    }

    /// <summary>The chat shortcut and the tray row: opens it in front, or closes it when it is in front.</summary>
    public void Toggle()
    {
        if (Visible && WindowState != FormWindowState.Minimized && ContainsFocus)
        {
            Hide();
            return;
        }
        ShowInFront();
    }

    public void ShowInFront()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        UpdateHeader();
        Activate();
        BringToFront();
        _input.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The window lives as long as the app: the ✕ only hides it, the chats stay where they are.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _cts?.Cancel();
        base.OnFormClosing(e);
    }

    /// <summary>Offline mode was just switched on: a running cloud answer is cancelled like with Esc.</summary>
    public void CancelIfCloudWhileOffline()
    {
        if (_cts != null && _settings.OfflineMode && _runningEngine != Core.Engine.Local)
        {
            Core.Log.Info("Chat: stopped, offline mode switched on");
            _cts.Cancel();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (_cts != null) _cts.Cancel();
            else if (_commands.Visible) HideCommands();
            else Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------- input and commands ----------------

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyData is (Keys.Control | Keys.V) or (Keys.Shift | Keys.Insert) && TryPasteAttachments())
        {
            e.SuppressKeyPress = e.Handled = true;
            return;
        }
        if (_commands.Visible && e.KeyCode is Keys.Down or Keys.Up)
        {
            int n = _commands.Items.Count;
            _commands.SelectedIndex = ((_commands.SelectedIndex < 0 ? 0 : _commands.SelectedIndex) + (e.KeyCode == Keys.Down ? 1 : n - 1)) % n;
            e.SuppressKeyPress = e.Handled = true;
            return;
        }
        if (_commands.Visible && e.KeyCode == Keys.Tab)
        {
            AcceptCommand();
            e.SuppressKeyPress = e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.SuppressKeyPress = e.Handled = true;
            if (_commands.Visible && _commands.SelectedItem is CommandItem c && !_input.Text.Trim().Equals(c.Command, StringComparison.OrdinalIgnoreCase))
                _input.Text = c.Command;
            Submit();
        }
        else if (e.KeyCode == Keys.Enter && e.Shift)
        {
            int at = _input.SelectionStart;
            _input.SelectedText = "\r\n";
            _input.SelectionStart = at + 2;
            e.SuppressKeyPress = e.Handled = true;
        }
    }

    private sealed record CommandItem(string Command, string Description);

    private void UpdateCommands()
    {
        var found = Core.ChatCommands.Suggestions(_input.Text, _userCommands);
        if (found.Count == 0)
        {
            HideCommands();
            return;
        }
        _commands.BeginUpdate();
        _commands.Items.Clear();
        foreach (var (command, description) in found) _commands.Items.Add(new CommandItem(command, description));
        _commands.SelectedIndex = 0;
        _commands.EndUpdate();
        _commands.Parent!.Height = Math.Min(found.Count, 8) * _commands.ItemHeight + 2;
        _commands.Visible = true;
    }

    private void HideCommands()
    {
        _commands.Visible = false;
        if (_commands.Parent != null) _commands.Parent.Height = 0;
    }

    private void AcceptCommand()
    {
        if (_commands.SelectedItem is not CommandItem c) return;
        _input.Text = c.Command;
        _input.SelectionStart = _input.TextLength;
        HideCommands();
        _input.Focus();
        if (c.Command is not ("/model" or "/think")) Submit(); // commands with an argument are completed, not run
    }

    private void Submit()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 && _pending.Count == 0) return;
        HideCommands();
        if (Core.ChatCommands.TryParse(text, out var kind, out var argument))
        {
            _input.Clear();
            Run(kind, argument);
            return;
        }
        if (_cts != null) return; // one answer at a time; Esc stops the running one
        if (Core.ChatCommands.TryParseUser(text, _userCommands, out var command, out var args))
        {
            RunUserCommand(command, args);
            return;
        }
        _input.Clear();
        _ = SendAsync(text, TakePending());
    }

    private void Run(Core.ChatCommandKind kind, string argument)
    {
        Core.Log.Info($"Chat command {kind}");
        switch (kind)
        {
            case Core.ChatCommandKind.New:
                NewChat();
                break;
            case Core.ChatCommandKind.Model:
                if (argument.Length > 0 && MatchModel(argument) is { } match) Pick(match);
                else ShowModelMenu();
                break;
            case Core.ChatCommandKind.Gemini:
                Pick(new Core.PickerModel(Core.Engine.Gemini, Core.Settings.DefaultPromptModel, "", ""));
                break;
            case Core.ChatCommandKind.Gpt:
                Pick(new Core.PickerModel(Core.Engine.OpenAI, Core.OpenAiModels.DefaultPromptModel, "", ""));
                break;
            case Core.ChatCommandKind.Settings:
                _openSettings();
                break;
            case Core.ChatCommandKind.Pin:
                _pinned = !_pinned;
                UpdateHeader();
                break;
            case Core.ChatCommandKind.Unpin:
                _pinned = false;
                UpdateHeader();
                break;
            case Core.ChatCommandKind.Meeting:
                bool was = _meetingActive();
                _toggleMeeting();
                if (!was && _meetingActive()) AttachToLiveMeeting(); // ChatWindow.Meeting.cs: this chat becomes the meeting chat
                SetStatus(was ? "Meeting wird beendet …" : _meetingActive() ? "Meeting läuft. Frag hier jederzeit dazu, mit /meeting beenden." : "", Tone.Secondary);
                UpdateMeetingUi();
                break;
            case Core.ChatCommandKind.ModelAlias:
                if (ChatModels().FirstOrDefault(m => m.Id == argument) is { } alias) Pick(alias);
                break;
            case Core.ChatCommandKind.Think:
                RunThink(argument);
                break;
            case Core.ChatCommandKind.Attach:
                PickFiles();
                break;
            case Core.ChatCommandKind.Screenshot:
                _ = CaptureScreenshotAsync();
                break;
            case Core.ChatCommandKind.Feedback:
                SendFeedback();
                break;
            case Core.ChatCommandKind.Copy:
                if (_session.Messages.Count == 0) SetStatus("Der Chat ist noch leer.", Tone.Secondary);
                else
                {
                    Clipboard.SetText(Core.ChatText.ToMarkdown(_session));
                    SetStatus("Chatverlauf als Markdown in der Zwischenablage.", Tone.Good);
                }
                break;
        }
    }

    // ---------------- models ----------------

    private IReadOnlyList<Core.PickerModel> ChatModels() =>
        Core.ModelPickers.Prompt(_offline.SelectedLlm.DisplayName);

    private Core.PickerModel? CurrentModel() =>
        Core.ModelPickers.Find(ChatModels(), _settings.EffectiveChat.Engine, _settings.EffectiveChat.Model);

    private string ModelName => CurrentModel()?.Name ?? _settings.EffectiveChat.Model;

    /// <summary>„/model 3.1 flash“: the model whose name contains every word, else null.</summary>
    private Core.PickerModel? MatchModel(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return ChatModels().FirstOrDefault(m => words.All(w => m.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private void ShowModelMenu()
    {
        _modelMenu.Items.Clear();
        if (_settings.OfflineMode)
        {
            _modelMenu.Items.Add(new ToolStripMenuItem("Offline-Modus ist an: es antwortet das Sprachmodell auf diesem PC") { Enabled = false });
        }
        else
        {
            var current = CurrentModel();
            Core.Engine? last = null;
            foreach (var m in ChatModels())
            {
                if (last != null && m.Engine != last) _modelMenu.Items.Add(new ToolStripSeparator());
                last = m.Engine;
                var provider = m.Engine switch { Core.Engine.Gemini => "Google", Core.Engine.OpenAI => "OpenAI", _ => "Auf diesem PC" };
                var item = new ToolStripMenuItem($"{m.Name}  ·  {provider}") { Checked = m == current, ToolTipText = m.Description };
                var model = m;
                item.Click += (_, _) => Pick(model);
                _modelMenu.Items.Add(item);
            }
        }
        Theme.Menu(_modelMenu);
        _modelMenu.Show(_model, new Point(0, _model.Height));
    }

    private void Pick(Core.PickerModel model)
    {
        _settings.Chat = _settings.Chat.Choose(Core.ModelStep.Text, model.Engine, model.Id);
        _save();
        UpdateHeader();
        SetStatus($"Es antwortet jetzt {ModelName}.", Tone.Secondary);
    }

    private void UpdateHeader()
    {
        _model.Text = ModelName + " ▾";
        _pin.Text = _pinned ? "📌 Angeheftet (/unpin)" : "";
        UpdateHeaderExtras();
        _defaultStatus = "Enter sendet · Umschalt+Enter neue Zeile · / für Befehle · Esc schließt";
        if (_cts == null) SetStatus(_defaultStatus, Tone.Secondary);
    }

    private void SetStatus(string text, Tone tone)
    {
        _status.Text = text.Length > 0 ? text : _defaultStatus;
        _status.Tone = text.Length > 0 ? tone : Tone.Secondary;
    }

    // ---------------- sending ----------------

    private async Task SendAsync(string text, IReadOnlyList<Core.ChatAttachment>? attachments = null)
    {
        var session = _session;
        session.Add(new Core.ChatMessage(Core.ChatRole.User, text, DateTimeOffset.Now, Attachments: attachments is { Count: > 0 } ? attachments : null));
        Persist(session);
        var engine = _settings.EffectiveChat.Engine;
        var model = _settings.EffectiveChat.Model;
        var modelName = ModelName;
        var system = Core.ChatPrompts.Effective(_settings.ChatSystemPrompt, offline: engine == Core.Engine.Local);
        system = WithMeetingContext(system, session); // ChatWindow.Meeting.cs
        var window = Core.ChatText.Window(session.Messages, engine == Core.Engine.Local ? Core.ChatText.LocalWindowChars : 200_000);

        var cts = _cts = new CancellationTokenSource();
        _runningEngine = engine;
        _send.Enabled = false;
        SetStatus("Denkt nach … (Esc bricht ab)", Tone.Secondary);
        Render();
        AppendRun(modelName + "\n", Theme.Strong, Theme.Secondary);
        var partial = new System.Text.StringBuilder();
        Core.Log.Info($"Chat: send to {engine} {model}, {window.Count} messages");
        try
        {
            Core.ChatReply reply;
            if (engine == Core.Engine.Local)
            {
                // The local model answers in one piece (no stream); the Mac's offline chat behaves the same way.
                var answer = await _offline.CompleteAsync(system, Core.ChatText.Flatten("", window), 2048, cts.Token);
                reply = new Core.ChatReply(answer.Trim(), Array.Empty<Core.ChatSource>());
            }
            else
            {
                reply = await _client.SendAsync(engine, model, system, window, piece => PostToUi(() =>
                {
                    if (_cts != cts || _session != session) return;
                    partial.Append(piece);
                    AppendRun(piece, Theme.Body, Theme.Text);
                    if (partial.Length == piece.Length) SetStatus("Schreibt … (Esc bricht ab)", Tone.Secondary);
                }), _settings.ChatWebSearch, cts.Token, session.ThinkingLevel);
            }
            if (IsDisposed) return;
            session.Add(new Core.ChatMessage(Core.ChatRole.Model, reply.Text.Trim(), DateTimeOffset.Now, reply.Sources, modelName));
            Persist(session);
            SetStatus(engine == Core.Engine.Local && window.Any(m => m.Attachments?.Any(a => !Core.ChatAttachments.IsText(a)) == true)
                ? "Bilder und PDFs kann das Modell auf diesem PC nicht lesen; es hat nur den Text bekommen." : "", Tone.Secondary);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Core.Log.Info("Chat: stopped by the user");
            if (IsDisposed) return;
            if (partial.Length > 0)
            {
                session.Add(new Core.ChatMessage(Core.ChatRole.Model, partial.ToString().Trim() + " …", DateTimeOffset.Now, null, modelName));
                Persist(session);
            }
            SetStatus("Abgebrochen.", Tone.Secondary);
        }
        catch (Exception e)
        {
            if (e is Core.GeminiException g) Core.Log.Error($"Chat: {g.Provider} {g.Kind}");
            else if (e is not (Core.OfflineNotReadyException or Core.LocalLlmException)) Core.Log.Error("Chat failed", e);
            if (IsDisposed) return;
            SetStatus(Core.RunOutcomes.ForFailure(e, audioKept: false, Core.FailureContext.Chat).Text, Tone.Bad);
        }
        finally
        {
            if (_cts == cts) { _cts = null; _runningEngine = null; }
            cts.Dispose();
            if (!IsDisposed) _send.Enabled = true;
            if (!IsDisposed && _session == session) Render();
        }
    }

    protected override void Dispose(bool disposing)
    {
        // Quitting while an answer streams: stop the request.
        if (disposing) _cts?.Cancel();
        base.Dispose(disposing);
    }

    // Stream pieces arrive on the HTTP thread; after the app quit there is no window to draw into.
    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void Persist(Core.ChatSession session)
    {
        try
        {
            _store.Save(session);
        }
        catch (Exception e)
        {
            Core.Log.Error("Chat could not be saved", e);
        }
        ShowInList(session);
    }

    /// <summary>
    /// Puts a just saved chat at its place in the list. WHY not <see cref="ReloadSessions"/>: that reads and parses every
    /// chat file, and Persist runs twice per message (question and answer), on the UI thread.
    /// </summary>
    private void ShowInList(Core.ChatSession session)
    {
        var all = _sessions.Items.Cast<Core.ChatSession>().Where(s => s.Id != session.Id).Append(session)
            .OrderByDescending(s => s.Updated).ToList();
        FillList(all);
    }

    // ---------------- sessions ----------------

    private void NewChat()
    {
        _cts?.Cancel();
        _session = Core.ChatSession.New(DateTimeOffset.Now);
        _sessions.ClearSelected();
        Render();
        UpdateHeader();
        SetStatus("", Tone.Secondary);
        _input.Focus();
    }

    private void Open(Core.ChatSession session)
    {
        _session = _store.Load(session.Id) ?? session;
        Render();
        UpdateHeader();
        SetStatus("", Tone.Secondary);
    }

    private void DeleteSelected()
    {
        if (_sessions.SelectedItem is not Core.ChatSession s) return;
        _holdOpen = true;
        var answer = MessageBox.Show(this, $"„{s.Title}“ löschen?", "Chat löschen", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        _holdOpen = false;
        if (answer != DialogResult.OK) return;
        try { _store.Delete(s.Id); }
        catch (Exception e) { Core.Log.Error("Chat could not be deleted", e); }
        if (s.Id == _session.Id) NewChat();
        FillList(_sessions.Items.Cast<Core.ChatSession>().Where(x => x.Id != s.Id).ToList());
    }

    private void ReloadSessions()
    {
        IReadOnlyList<Core.ChatSession> all;
        try { all = _store.List(); }
        catch (Exception e)
        {
            Core.Log.Error("Chats could not be listed", e);
            all = Array.Empty<Core.ChatSession>();
        }
        FillList(all);
    }

    private void FillList(IReadOnlyList<Core.ChatSession> all)
    {
        _sessions.BeginUpdate();
        _sessions.Items.Clear();
        foreach (var s in all) _sessions.Items.Add(s);
        var current = all.FirstOrDefault(s => s.Id == _session.Id);
        if (current != null) _sessions.SelectedItem = current;
        _sessions.EndUpdate();
    }

    private void DrawSession(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _sessions.Items[e.Index] is not Core.ChatSession s) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Theme.NavSelected : Theme.Window)) e.Graphics.FillRectangle(back, e.Bounds);
        var r = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 4, e.Bounds.Width - 12, e.Bounds.Height - 8);
        TextRenderer.DrawText(e.Graphics, s.Title, Theme.Body, r with { Height = r.Height / 2 + 2 }, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, When(s.Updated), Theme.Small, r with { Y = r.Y + r.Height / 2 + 2, Height = r.Height / 2 }, Theme.Secondary,
            TextFormatFlags.Left | TextFormatFlags.SingleLine);
    }

    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today ? $"Heute, {local:HH:mm}"
            : local.Date == DateTime.Today.AddDays(-1) ? $"Gestern, {local:HH:mm}"
            : local.ToString("dd.MM.yyyy");
    }

    private void DrawCommand(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _commands.Items[e.Index] is not CommandItem c) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Theme.ItemSelected : Theme.Card)) e.Graphics.FillRectangle(back, e.Bounds);
        var r = e.Bounds with { X = e.Bounds.X + 8, Width = e.Bounds.Width - 12 };
        TextRenderer.DrawText(e.Graphics, c.Command, Theme.Strong, r with { Width = 110 }, selected ? Theme.OnSelected : Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, c.Description, Theme.Body, r with { X = r.X + 116, Width = r.Width - 116 }, Theme.Secondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // ---------------- drawing the conversation ----------------

    private static readonly Font Mono = MonoFont();
    private static Font MonoFont()
    {
        var font = new Font("Cascadia Mono", 10f);
        if (font.Name == "Cascadia Mono") return font;
        font.Dispose();
        return new Font("Consolas", 10f);
    }
    private static readonly Font HeadingFont = new("Segoe UI Semibold", 12f);

    // True while Render rebuilds the whole transcript: the runs neither scroll nor paint one by one.
    private bool _rendering;

    private void Render()
    {
        _rendering = true;
        // WHY redraw off: every run selected, scrolled and repainted the box; a long chat repainted it hundreds of times.
        SetRedraw(_transcript, false);
        try { RenderRuns(); }
        finally
        {
            _rendering = false;
            SetRedraw(_transcript, true);
        }
        ScrollToEnd();
        _transcript.Invalidate();
    }

    private static void SetRedraw(Control control, bool on)
    {
        const int WM_SETREDRAW = 0x000B;
        if (control.IsHandleCreated) SendMessage(control.Handle, WM_SETREDRAW, on ? 1 : 0, 0);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private void RenderRuns()
    {
        _transcript.Clear();
        if (_session.Messages.Count == 0)
        {
            AppendRun("Frag etwas, lass einen Text schreiben oder übersetzen.\n\n", Theme.Body, Theme.Secondary);
            AppendRun("Tippe / für die Befehle, z. B. /meeting für ein Live-Meeting oder /model für ein anderes Modell.", Theme.Small, Theme.Secondary);
        }
        foreach (var m in _session.Messages)
        {
            if (m.Role == Core.ChatRole.User)
            {
                AppendRun("Du\n", Theme.Strong, Theme.Accent);
                if (m.Attachments is { Count: > 0 } files)
                    AppendRun("📎 " + string.Join(", ", files.Select(f => f.FileName)) + "\n", Theme.Small, Theme.Secondary);
                AppendRun(m.Text + "\n\n", Theme.Body, Theme.Text);
                continue;
            }
            AppendRun((m.Model ?? "Antwort") + "\n", Theme.Strong, Theme.Secondary);
            foreach (var span in Core.ChatMarkdown.Parse(m.Text))
            {
                switch (span.Style)
                {
                    case Core.ChatSpanStyle.Bold: AppendRun(span.Text, Theme.Strong, Theme.Text); break;
                    case Core.ChatSpanStyle.Code: AppendRun(span.Text, Mono, Theme.Text, Theme.Field); break;
                    case Core.ChatSpanStyle.CodeBlock: AppendRun(span.Text, Mono, Theme.Text, Theme.Field); break;
                    case Core.ChatSpanStyle.Heading: AppendRun(span.Text, HeadingFont, Theme.Text); break;
                    default: AppendRun(span.Text, Theme.Body, Theme.Text); break;
                }
            }
            if (m.Sources is { Count: > 0 } sources)
            {
                AppendRun("\nQuellen\n", Theme.Small, Theme.Secondary);
                int n = 1;
                foreach (var s in sources.Take(8)) AppendRun($"{n++}. {s.Title} – {s.Url}\n", Theme.Small, Theme.Secondary);
            }
            AppendRun("\n\n", Theme.Body, Theme.Text);
        }
    }

    private void AppendRun(string text, Font font, Color color, Color? back = null)
    {
        _transcript.Select(_transcript.TextLength, 0);
        _transcript.SelectionFont = font;
        _transcript.SelectionColor = color;
        _transcript.SelectionBackColor = back ?? _transcript.BackColor;
        _transcript.SelectedText = text.ReplaceLineEndings("\n");
        if (!_rendering) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        _transcript.Select(_transcript.TextLength, 0);
        _transcript.ScrollToCaret();
    }

    private void CopyLastAnswer(bool blockOnly)
    {
        var last = _session.Messages.LastOrDefault(m => m.Role == Core.ChatRole.Model)?.Text;
        var text = blockOnly ? Core.ChatMarkdown.LastCodeBlock(last) : last;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus(blockOnly ? "Die letzte Antwort hat keinen Textblock." : "Noch keine Antwort.", Tone.Secondary);
            return;
        }
        Clipboard.SetText(text);
        SetStatus(blockOnly ? "Textblock kopiert." : "Antwort kopiert.", Tone.Good);
    }

    private static void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true }); }
        catch (Exception e) { Core.Log.Error("Link could not be opened", e); }
    }
}
