using static WhisperShortcut.NativeMethods;

namespace WhisperShortcut;

/// <summary>
/// Picks the hotkey that starts and ends a meeting. A dialog of its own instead of a row in the
/// settings window: the meeting key is optional (none by default, the tray menu is enough for most),
/// and the settings window's hotkey page is shared by several changes in flight.
/// </summary>
internal sealed class MeetingHotkeyDialog : Form
{
    /// <summary>
    /// Shows the dialog. Returns the new stored hotkey, "" when the user removed it, or null when the
    /// dialog was closed without a change. <paramref name="taken"/> are the stored hotkeys of the other
    /// functions; the same combination cannot be picked twice.
    /// </summary>
    public static string? Ask(string current, IReadOnlyCollection<string> taken)
    {
        using var dialog = new MeetingHotkeyDialog(current, taken);
        dialog.ShowDialog();
        return dialog._result;
    }

    private readonly KeyBox _box;
    private readonly ThemedLabel _error;
    private readonly IReadOnlyCollection<string> _taken;
    private string? _result;

    private MeetingHotkeyDialog(string current, IReadOnlyCollection<string> taken)
    {
        _taken = taken;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "WhisperShortcut";
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = true; // opened from the tray: without a taskbar button it can vanish behind other windows
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(24, 20, 24, 18) };
        root.Controls.Add(new ThemedLabel
        {
            Text = "Tastenkürzel für Meetings", Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Margin = new Padding(0, 0, 0, 6),
        });
        root.Controls.Add(new ThemedLabel
        {
            Text = "Startet ein Meeting und beendet es wieder. Ohne Kürzel geht das über das Symbol unten rechts → Meeting.",
            AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 14),
        });
        _box = new KeyBox(current) { Width = 220, AccessibleName = "Tastenkürzel Meeting" };
        root.Controls.Add(new FieldFrame(_box) { Width = 220, Dock = DockStyle.None, Margin = new Padding(0, 0, 0, 6) });
        _error = new ThemedLabel { AutoSize = true, MaximumSize = new Size(420, 0), Tone = Tone.Bad, Margin = new Padding(0, 0, 0, 14), Text = "" };
        root.Controls.Add(_error);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
        var save = new FluentButton("Speichern", accent: true) { MinimumSize = new Size(112, 32), Margin = new Padding(0) };
        var remove = new FluentButton("Kein Kürzel") { MinimumSize = new Size(112, 32), Margin = new Padding(8, 0, 0, 0), Enabled = current.Length > 0 };
        var cancel = new FluentButton("Abbrechen") { MinimumSize = new Size(112, 32), Margin = new Padding(8, 0, 0, 0) };
        save.Click += (_, _) => Save();
        remove.Click += (_, _) => { _result = ""; Close(); };
        cancel.Click += (_, _) => Close();
        buttons.Controls.AddRange(new Control[] { save, remove, cancel });
        root.Controls.Add(buttons);

        Controls.Add(root);
        CancelButton = cancel;
        ResumeLayout(true);

        Load += (_, _) => Theme.Apply(this);
        Shown += (_, _) => { Activate(); _box.Focus(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }

    private void Save()
    {
        if (!Hotkey.TryParse(_box.Value, out var key))
        {
            _error.Text = "Bitte in das Feld klicken und die Tasten drücken, z. B. Strg+Umschalt+M.";
            return;
        }
        if (_taken.Any(t => Hotkey.TryParse(t, out var other) && other == key))
        {
            _error.Text = $"{key.ToDisplayString()} benutzt schon eine andere Funktion. Bitte ein anderes Kürzel wählen.";
            return;
        }
        _result = key.ToString();
        Close();
    }

    /// <summary>
    /// Two of Ctrl/Alt/Shift, one of them Ctrl or Alt, and not Ctrl+Alt alone (AltGr on German
    /// keyboards). Same rule as the settings window's hotkey fields.
    /// </summary>
    private static bool IsSafe(Hotkey hotkey)
    {
        uint m = hotkey.Modifiers;
        int count = ((m & MOD_CONTROL) != 0 ? 1 : 0) + ((m & MOD_ALT) != 0 ? 1 : 0) + ((m & MOD_SHIFT) != 0 ? 1 : 0);
        if ((m & (MOD_CONTROL | MOD_ALT)) == (MOD_CONTROL | MOD_ALT) && (m & MOD_SHIFT) == 0) return false;
        return count >= 2 && (m & (MOD_CONTROL | MOD_ALT)) != 0;
    }

    /// <summary>Text box that records a key combination instead of text (like the settings window's).</summary>
    private sealed class KeyBox : TextBox
    {
        public string Value { get; private set; }

        public KeyBox(string stored)
        {
            Value = stored;
            Text = stored.Length == 0 ? "" : Hotkey.Display(stored);
            PlaceholderText = "Tasten drücken …";
            ReadOnly = true;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
        }

        protected override bool IsInputKey(Keys keyData) => true;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Focused && (keyData & Keys.Alt) != 0)
            {
                Apply(keyData & Keys.KeyCode, keyData & Keys.Modifiers);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (e.KeyCode == Keys.Escape && e.Modifiers == Keys.None)
            {
                FindForm()?.Close();
                return;
            }
            if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None)
            {
                (FindForm() as MeetingHotkeyDialog)?.Save();
                return;
            }
            if (e.KeyCode == Keys.Tab && e.Modifiers is Keys.None or Keys.Shift)
            {
                FindForm()?.SelectNextControl(this, e.Modifiers == Keys.None, true, true, true);
                return;
            }
            Apply(e.KeyCode, e.Modifiers);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (Text.EndsWith('+')) Text = Value.Length == 0 ? "" : Hotkey.Display(Value);
        }

        private void Apply(Keys key, Keys modifiers)
        {
            var prefix = (modifiers.HasFlag(Keys.Control) ? "Strg+" : "") + (modifiers.HasFlag(Keys.Alt) ? "Alt+" : "")
                + (modifiers.HasFlag(Keys.Shift) ? "Umschalt+" : "");
            if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin or Keys.None)
            {
                Text = prefix;
                return;
            }
            var stored = (modifiers.HasFlag(Keys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(Keys.Alt) ? "Alt+" : "")
                + (modifiers.HasFlag(Keys.Shift) ? "Shift+" : "") + key;
            if (!Hotkey.TryParse(stored, out var parsed) || !IsSafe(parsed))
            {
                Text = "Bitte Strg+Umschalt+… verwenden";
                return;
            }
            Value = parsed.ToString();
            Text = parsed.ToDisplayString();
        }
    }
}
