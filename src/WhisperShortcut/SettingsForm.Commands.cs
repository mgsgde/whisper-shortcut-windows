namespace WhisperShortcut;

/// <summary>
/// Chat page, section „Befehle“ (Mac Settings/Tabs/Chat/UserCommandsSection): the user's own chat commands, one Markdown file
/// per command in the commands folder. Nothing here is a setting: the files are the commands, so there is no snapshot or
/// restore; the chat re-reads the folder whenever its window comes to the front.
/// </summary>
internal sealed partial class SettingsForm
{
    private readonly Core.UserCommandStore _commands = new(Core.UserCommandStore.DefaultRoot);
    private TableLayoutPanel _commandList = null!;

    private void BuildCommandsSection(TableLayoutPanel page)
    {
        SectionIcons.TryAdd("Befehle", "");
        page.Controls.Add(Section("Befehle", subtitle: "Deine eigenen Anweisungen auf einen Klick. Jede Markdown-Datei im Befehlsordner "
            + "wird im Chat zu einem Slash-Befehl. Befehle für Meetings bekommen zusätzlich eine Taste über dem Eingabefeld des Meeting-Chats."));
        var card = new Card();
        _commandList = Block();
        card.AddRow(_commandList);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        var add = new FluentButton("Neuer Befehl …") { MinimumSize = new Size(ButtonWidth, 32), AccessibleName = "Neuen Befehl aus der Vorlage anlegen" };
        add.Click += (_, _) => NewCommand();
        var folder = new FluentButton("Befehlsordner öffnen") { MinimumSize = new Size(ButtonWidth, 32), Margin = new Padding(8, 0, 0, 0) };
        folder.Click += (_, _) => OpenFolder(_commands.Root);
        var reload = new FluentButton("Neu laden") { MinimumSize = new Size(96, 32), Margin = new Padding(8, 0, 0, 0) };
        new ToolTip().SetToolTip(reload, "Ordner neu einlesen. Der Chat liest ihn auch jedes Mal neu ein, wenn sein Fenster nach vorn kommt.");
        reload.Click += (_, _) => RefreshCommands();
        buttons.Controls.Add(add);
        buttons.Controls.Add(folder);
        buttons.Controls.Add(reload);
        var footer = Block();
        footer.Controls.Add(buttons);
        footer.Controls.Add(Wrap("Dateiname = Befehlsname (anamnese.md → /anamnese). Optionale Kopfzeilen: label (Text der Taste), description, "
            + "scope (meeting, chat oder both). Alles unter dem Kopf geht als deine Nachricht an das gewählte Chat-Modell, auch an das Modell "
            + "auf diesem PC. Im Meeting-Chat sieht das Modell zusätzlich das ganze Transkript.", 2 * RowPadding, small: true));
        card.AddRow(footer);
        page.Controls.Add(card);
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        IReadOnlyList<Core.UserCommand> commands;
        IReadOnlyList<Core.UserCommandIssue> issues;
        try { (commands, issues) = _commands.Load(Core.ChatCommands.ReservedNames); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Core.Log.Error("Commands folder could not be read", e);
            (commands, issues) = (Array.Empty<Core.UserCommand>(), Array.Empty<Core.UserCommandIssue>());
        }
        _commandList.SuspendLayout();
        foreach (Control c in _commandList.Controls) c.Dispose();
        _commandList.Controls.Clear();
        if (commands.Count == 0)
            _commandList.Controls.Add(Wrap("Noch keine Befehle. Leg einen aus der Vorlage an oder leg .md-Dateien in den Befehlsordner.", 2 * RowPadding, small: true));
        foreach (var command in commands) _commandList.Controls.Add(CommandRow(command));
        foreach (var issue in issues)
        {
            var line = Wrap($"{issue.FileName}: {issue.Message}", 2 * RowPadding, small: true);
            ((ThemedLabel)line).Tone = issue.IsWarning ? Tone.Secondary : Tone.Bad;
            _commandList.Controls.Add(line);
        }
        _commandList.ResumeLayout(true);
        Theme.Apply(_commandList);
        FitHints();
    }

    private Control CommandRow(Core.UserCommand command)
    {
        var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var scope = command.Scope switch
        {
            Core.UserCommandScope.Meeting => "Meeting",
            Core.UserCommandScope.Chat => "Chat",
            _ => "Chat + Meeting",
        };
        text.Controls.Add(new ThemedLabel { Text = $"{command.Slash}   ·   {scope}", Font = Theme.Strong, AutoSize = true, Margin = new Padding(0), UseMnemonic = false });
        if (command.Description.Length > 0)
            text.Controls.Add(new ThemedLabel { Text = command.Description, Font = Theme.Small, Tone = Tone.Secondary, AutoSize = true, Margin = new Padding(0, 2, 0, 0), UseMnemonic = false });
        row.Controls.Add(text, 0, 0);
        var edit = new FluentButton("Bearbeiten") { MinimumSize = new Size(96, 30), Margin = new Padding(8, 0, 0, 0), AccessibleName = $"{command.Slash} bearbeiten" };
        edit.Click += (_, _) => OpenLocal(command.FilePath);
        var delete = new FluentButton("Löschen") { MinimumSize = new Size(96, 30), Margin = new Padding(8, 0, 0, 0), AccessibleName = $"{command.Slash} löschen" };
        delete.Click += (_, _) => DeleteCommand(command);
        row.Controls.Add(edit, 1, 0);
        row.Controls.Add(delete, 2, 0);
        return row;
    }

    /// <summary>Mac „New Command…“: a file from the template, opened in the editor.</summary>
    private void NewCommand()
    {
        try
        {
            var path = _commands.CreateFromTemplate();
            Core.Log.Info($"Command created: {Path.GetFileName(path)}");
            RefreshCommands();
            OpenLocal(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Core.Log.Error("Command could not be created", e);
            Warn("Der Befehl konnte nicht angelegt werden. Details stehen im Protokoll.");
        }
    }

    /// <summary>Mac: moves the file to the Trash. Windows: the Recycle Bin, so it can be restored from there.</summary>
    private void DeleteCommand(Core.UserCommand command)
    {
        if (MessageBox.Show(this, $"{command.Slash} löschen? Die Datei kommt in den Papierkorb.", "Befehl löschen",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(command.FilePath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            Core.Log.Info($"Command deleted: {command.Slash}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Core.Log.Error("Command could not be deleted", e);
            Warn("Der Befehl konnte nicht gelöscht werden. Details stehen im Protokoll.");
        }
        RefreshCommands();
    }
}
