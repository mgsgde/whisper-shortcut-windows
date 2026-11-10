using static WhisperShortcut.NativeMethods;

namespace WhisperShortcut;

/// <summary>
/// The cards and pills that float above everything but never take focus: a stolen focus breaks the paste into
/// the user's window and closes the chat. The window styles live here once, so the four overlays cannot drift apart.
/// </summary>
internal abstract class OverlayForm : Form
{
    private readonly bool _layered;
    private readonly bool _clickThrough;

    /// <param name="layered">WS_EX_LAYERED: the overlay paints its own per-pixel alpha.</param>
    /// <param name="clickThrough">WS_EX_TRANSPARENT from the start: mouse clicks fall through to the window below.</param>
    protected OverlayForm(bool layered, bool clickThrough)
    {
        _layered = layered;
        _clickThrough = clickThrough;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // WHY no TopMost = true: WinForms then activates the window on Show despite ShowWithoutActivation, which
        // took the focus from the chat (it closes on focus loss) and from the target window. WS_EX_TOPMOST below keeps it on top.
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (_layered) cp.ExStyle |= WS_EX_LAYERED;
            if (_clickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // A click on the card must not activate it: the user's window keeps focus for the paste.
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    protected float S => DeviceDpi / 96f;

    /// <summary>Switches WS_EX_TRANSPARENT on a window that already exists (the card needs clicks, the bare pill does not).</summary>
    protected void SetClickThrough(bool through)
    {
        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        int want = through ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (want != ex) SetWindowLong(Handle, GWL_EXSTYLE, want);
    }

    /// <summary>Rounded corners and a thin border in the card's colour; call it from OnHandleCreated.</summary>
    protected void ApplyRoundedBorder(Color border)
    {
        int round = 2; // DWMWCP_ROUND, Windows 11; Windows 10 ignores it
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        int borderColor = ColorTranslator.ToWin32(border);
        DwmSetWindowAttribute(Handle, 34, ref borderColor, sizeof(int));
    }
}
