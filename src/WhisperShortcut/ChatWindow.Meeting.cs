namespace WhisperShortcut;

/// <summary>
/// The Mac chat features around one chat: the meeting chat (a chat attached to a meeting sees its transcript, live while it
/// records, then summary and transcript), /think (thinking depth per chat), /feedback (WhatsApp with the end of this chat)
/// and the user's own commands (one .md file per command, Mac UserCommands). On Windows the chat from which /meeting starts
/// becomes the meeting chat; a meeting started from the tray offers „Fragen zum laufenden Meeting“ in the header.
/// </summary>
internal sealed partial class ChatWindow
{
    private readonly Core.MeetingStore _meetingStore = new(Core.MeetingStore.DefaultRoot);
    private readonly Core.UserCommandStore _commandStore = new(Core.UserCommandStore.DefaultRoot);
    private IReadOnlyList<Core.UserCommand> _userCommands = Array.Empty<Core.UserCommand>();
    private readonly LinkLabel _meetingLink = new() { AutoSize = true, Margin = new Padding(0, 4, 12, 4), Visible = false };
    private readonly ThemedLabel _meetingNote = new() { AutoSize = true, Tone = Tone.Secondary, Font = Theme.Small, Margin = new Padding(0, 6, 12, 4) };
    private readonly ThemedLabel _thinkNote = new() { AutoSize = true, Tone = Tone.Secondary, Font = Theme.Small, Margin = new Padding(0, 6, 12, 4) };
    private readonly FlowLayoutPanel _meetingButtons = new()
    {
        AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 0), Visible = false,
        AccessibleName = "Befehle für dieses Meeting",
    };
    private readonly System.Windows.Forms.Timer _meetingPoll = new() { Interval = 3000 };

    private void AddHeaderExtras(FlowLayoutPanel header)
    {
        header.Controls.Add(_thinkNote);
        header.Controls.Add(_meetingNote);
        header.Controls.Add(_meetingLink);
        _meetingLink.LinkClicked += (_, _) =>
        {
            AttachToLiveMeeting();
            UpdateMeetingUi();
            _input.Focus();
        };
        // A meeting started or stopped from the tray shows up here while the window is open.
        _meetingPoll.Tick += (_, _) => { if (Visible) UpdateMeetingUi(); };
        VisibleChanged += (_, _) => { if (Visible) _meetingPoll.Start(); else _meetingPoll.Stop(); };
        Disposed += (_, _) => _meetingPoll.Dispose();
    }

    private void UpdateHeaderExtras()
    {
        _thinkNote.Text = _session.ThinkingLevel is { } level && level != Core.ChatThinking.Default ? $"Denktiefe: {level}" : "";
        UpdateMeetingUi();
    }

    private void OnBroughtToFront()
    {
        ReloadUserCommands();
        UpdateMeetingUi();
    }

    // ---------------- meeting chat ----------------

    private Core.MeetingInfo? LiveMeeting()
    {
        try { return _meetingActive() ? _meetingStore.Live() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Core.Log.Error("Chat: live meeting could not be read", e);
            return null;
        }
    }

    /// <summary>Makes a chat the meeting chat of the recording meeting: this one when it is empty, else a new one.</summary>
    private void AttachToLiveMeeting()
    {
        if (LiveMeeting() is not { } live || _session.MeetingId == live.Id) return;
        if (_session.Messages.Count > 0 || _session.MeetingId != null) NewChat();
        _session.MeetingId = live.Id;
        _session.Title = "📝 " + live.Title;
        Core.Log.Info("Chat: attached to the live meeting");
        UpdateHeader();
    }

    private void UpdateMeetingUi()
    {
        var live = LiveMeeting();
        bool attached = _session.MeetingId != null;
        bool attachedLive = attached && live != null && live.Id == _session.MeetingId;
        _meetingNote.Text = attachedLive ? "📝 Meeting läuft · der Chat kennt das Transkript"
            : attached ? "📝 Meeting-Chat · Zusammenfassung und Transkript" : "";
        _meetingLink.Text = "📝 Fragen zum laufenden Meeting";
        _meetingLink.Visible = live != null && !attachedLive;

        // Mac: commands with scope meeting (or both) get a button above the input of a meeting chat.
        var buttons = attached ? _userCommands.Where(c => c.ShowsInMeeting).ToList() : new List<Core.UserCommand>();
        var shown = _meetingButtons.Controls.Cast<Control>().Select(c => c.Tag as string).ToList();
        if (!shown.SequenceEqual(buttons.Select(b => b.Name)))
        {
            _meetingButtons.SuspendLayout();
            foreach (Control c in _meetingButtons.Controls) c.Dispose();
            _meetingButtons.Controls.Clear();
            foreach (var command in buttons)
            {
                var b = new FluentButton(command.Label) { AutoSize = true, Height = 30, Margin = new Padding(0, 0, 6, 4), Tag = command.Name, AccessibleName = command.Label };
                new ToolTip().SetToolTip(b, command.Description.Length > 0 ? command.Description : command.Slash);
                var c = command;
                b.Click += (_, _) => { if (_cts == null) RunUserCommand(c, ""); };
                _meetingButtons.Controls.Add(b);
            }
            _meetingButtons.ResumeLayout(true);
            Theme.Apply(_meetingButtons);
        }
        _meetingButtons.Visible = buttons.Count > 0;
    }

    /// <summary>The system prompt plus the meeting's text (Mac: appended after a „---“ on every turn of a meeting chat).</summary>
    private string WithMeetingContext(string system, Core.ChatSession session)
    {
        if (session.MeetingId is not { } id) return system;
        try
        {
            return Core.MeetingChatContext.Append(system, Core.MeetingChatContext.For(_meetingStore, id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Core.Log.Error("Chat: meeting context could not be read", e);
            return system;
        }
    }

    // ---------------- /think ----------------

    /// <summary>Mac handleThinkCommand: sticky for this chat, stored with it.</summary>
    private void RunThink(string argument)
    {
        if (argument.Length == 0)
        {
            SetStatus($"Denktiefe für diesen Chat: {_session.ThinkingLevel}. Einstellen mit /think <Stufe> – {Core.ChatThinking.LevelList}.", Tone.Secondary);
            return;
        }
        if (!Core.ChatThinking.TryParse(argument, out var level))
        {
            SetStatus($"Unbekannte Denktiefe „{argument}“. Möglich sind: {Core.ChatThinking.LevelList}.", Tone.Bad);
            return;
        }
        _session.ThinkingLevel = level;
        if (_session.Messages.Count > 0) Persist(_session);
        Core.Log.Info($"Chat: /think {level}");
        UpdateHeader();
        SetStatus(level == Core.ChatThinking.Default
            ? "Denktiefe für diesen Chat auf den Standard des Modells zurückgesetzt."
            : $"Denktiefe für diesen Chat auf {level} gesetzt. Höher = gründlicher, aber langsamer und teurer.", Tone.Secondary);
    }

    // ---------------- /feedback ----------------

    /// <summary>Mac sendFeedbackFromChat: WhatsApp opens with the end of this chat prefilled; nothing is sent without the user.</summary>
    private void SendFeedback()
    {
        var context = Core.FeedbackLinks.ChatContext(_session.Messages);
        if (!TrayApp.OpenFeedback(Core.FeedbackChannel.WhatsApp, context))
        {
            SetStatus(Core.FeedbackLinks.OpenFailed, Tone.Bad);
            return;
        }
        SetStatus(context == null ? Core.FeedbackLinks.OpenedEmpty : Core.FeedbackLinks.OpenedWithChat, Tone.Secondary);
    }

    // ---------------- user commands ----------------

    private void ReloadUserCommands()
    {
        try
        {
            var (commands, issues) = _commandStore.Load(Core.ChatCommands.ReservedNames);
            _userCommands = commands;
            if (issues.Count > 0) Core.Log.Info($"Chat: {issues.Count} command file problem(s)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Core.Log.Error("Chat: commands folder could not be read", e);
        }
    }

    /// <summary>Mac: the command's text goes to the model as the user's message; text after the name is appended.</summary>
    private void RunUserCommand(Core.UserCommand command, string args)
    {
        if (!command.ShowsInChat && _session.MeetingId == null)
        {
            SetStatus(Core.UserCommandParser.NeedsMeeting(command.Slash), Tone.Bad);
            return;
        }
        Core.Log.Info($"Chat: user command {command.Slash}");
        _input.Clear();
        _ = SendAsync(Core.UserCommandParser.AppendingArguments(command.Body, args), TakePending());
    }
}
