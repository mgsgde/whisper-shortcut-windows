using System.Runtime.InteropServices;

namespace WhisperShortcut;

/// <summary>
/// A question with German buttons that name the outcome ("Speichern", "Verwerfen", "Zurück").
/// WHY not MessageBox: its buttons follow the Windows language ("Yes/No/Cancel" on an English Windows),
/// and "Ja/Nein" does not say what happens.
/// </summary>
internal sealed class ChoiceDialog : Form
{
    /// <summary>
    /// Index of the clicked button. The last button is the way back: Esc and the X answer it.
    /// <paramref name="danger"/>: the first button deletes something, so it is red and Enter does not press it.
    /// </summary>
    public static int Ask(IWin32Window owner, string heading, string text, string[] buttons, bool danger = false)
    {
        using var dialog = new ChoiceDialog(heading, text, buttons, danger);
        dialog.ShowDialog(owner);
        return dialog._choice;
    }

    private int _choice;

    private ChoiceDialog(string heading, string text, string[] labels, bool danger)
    {
        _choice = labels.Length - 1;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "WhisperShortcut";
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(24, 20, 24, 18) };
        root.Controls.Add(new ThemedLabel
        {
            Text = heading, Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Margin = new Padding(0, 0, 0, 6),
        });
        root.Controls.Add(new ThemedLabel
        {
            Text = text, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 20),
        });

        // Order as in Windows: the main action first, the way back last.
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
        var made = new FluentButton[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            made[i] = new FluentButton(labels[i], accent: i == 0 && !danger)
            {
                MinimumSize = new Size(112, 32), Danger = i == 0 && danger, Margin = new Padding(i == 0 ? 0 : 8, 0, 0, 0),
            };
            made[i].Click += (_, _) => { _choice = index; Close(); };
            buttons.Controls.Add(made[i]);
        }
        root.Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = danger ? made[^1] : made[0];
        CancelButton = made[^1];
        ResumeLayout(true);

        Load += (_, _) => Theme.Apply(this);
        Shown += (_, _) => ((Control)AcceptButton).Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }
}

/// <summary>
/// Hands the mouse wheel to the scrolling page around a control instead of letting the control eat it:
/// a multi-line text box once it cannot scroll further that way or is not being edited, a closed drop-down
/// always. Without it the wheel stopped on the instructions box (testers took its end for the end of the
/// settings) and changed the model or microphone when it passed over a drop-down while scrolling.
/// </summary>
internal sealed class WheelToPage : NativeWindow
{
    private const int WM_MOUSEWHEEL = 0x020A, SB_VERT = 1, SIF_ALL = 0x17;
    private readonly Control _control;

    /// <summary>Every multi-line text box and drop-down under <paramref name="root"/>.</summary>
    public static void Attach(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is TextBox { Multiline: true } or ComboBox) _ = new WheelToPage(child);
            Attach(child);
        }
    }

    // The control's handlers keep this object alive as long as the control.
    private WheelToPage(Control control)
    {
        _control = control;
        control.HandleCreated += (_, _) => AssignHandle(control.Handle);
        control.HandleDestroyed += (_, _) => ReleaseHandle();
        if (control.IsHandleCreated) AssignHandle(control.Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEWHEEL)
        {
            var page = Page();
            int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
            bool hand = _control switch
            {
                // Picking a value takes a click; the wheel over a closed drop-down only ever scrolls (or does nothing).
                ComboBox combo => !combo.DroppedDown,
                _ => page != null && (!_control.Focused || AtEnd(delta)),
            };
            if (hand)
            {
                if (page != null) SendMessage(page.Handle, m.Msg, m.WParam, m.LParam);
                return;
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>The nearest scrolling page that currently has something to scroll; none on the glossary page.</summary>
    private ScrollableControl? Page()
    {
        for (var c = _control.Parent; c != null; c = c.Parent)
            if (c is ScrollableControl { AutoScroll: true } s && s.VerticalScroll.Visible) return s;
        return null;
    }

    private bool AtEnd(int delta)
    {
        var info = new SCROLLINFO { cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(), fMask = SIF_ALL };
        if (!GetScrollInfo(_control.Handle, SB_VERT, ref info) || info.nMax - info.nMin + 1 <= info.nPage) return true;
        return delta > 0 ? info.nPos <= info.nMin : info.nPos + (int)info.nPage > info.nMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SCROLLINFO
    {
        public uint cbSize, fMask;
        public int nMin, nMax;
        public uint nPage;
        public int nPos, nTrackPos;
    }

    [DllImport("user32.dll")]
    private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
