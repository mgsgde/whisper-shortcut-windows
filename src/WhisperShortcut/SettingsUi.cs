using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WhisperShortcut;

/// <summary>
/// Colors and fonts of the settings window, following the Windows 11 settings app in light and dark.
/// Only the settings window uses it: the tray menu and the recording overlay keep their own look.
/// The controls below read the colors when they paint, so a theme switch only needs <see cref="Apply"/>.
/// </summary>
internal static class Theme
{
    public static bool Dark { get; private set; } = ReadDark();

    /// <summary>Re-reads "app mode" from Windows. True when it changed.</summary>
    public static bool Refresh()
    {
        bool dark = ReadDark();
        if (dark == Dark) return false;
        Dark = dark;
        return true;
    }

    private static bool ReadDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch { return false; }
    }

    private static Color Pick(int light, int dark) => Color.FromArgb(255, Color.FromArgb(Dark ? dark : light));

    public static Color Window => Pick(0xF3F3F3, 0x202020);
    public static Color Card => Pick(0xFBFBFB, 0x2B2B2B);
    public static Color CardBorder => Pick(0xE5E5E5, 0x1D1D1D);
    public static Color Divider => Pick(0xEAEAEA, 0x1D1D1D);
    public static Color Text => Pick(0x1B1B1B, 0xFFFFFF);
    public static Color Secondary => Pick(0x616161, 0xC5C5C5);
    public static Color Disabled => Pick(0xA0A0A0, 0x787878);
    public static Color Field => Pick(0xFFFFFF, 0x2F2F2F);
    public static Color FieldBorder => Pick(0xE0E0E0, 0x3A3A3A);
    public static Color FieldBottom => Pick(0x8A8A8A, 0x9A9A9A);
    public static Color ItemSelected => Pick(0xE8F1FA, 0x3A3A3A);
    public static Color NavSelected => Pick(0xEAEAEA, 0x2D2D2D);
    public static Color NavHover => Pick(0xEDEDED, 0x292929);
    public static Color Accent => Pick(0x005FB8, 0x60CDFF);
    public static Color AccentHover => Pick(0x1A6FC0, 0x5BBDEC);
    public static Color AccentPressed => Pick(0x3381CA, 0x55A9D1);
    public static Color AccentText => Pick(0xFFFFFF, 0x000000);
    public static Color Button => Pick(0xFEFEFE, 0x373737);
    public static Color ButtonHover => Pick(0xF6F6F6, 0x3D3D3D);
    public static Color ButtonPressed => Pick(0xF0F0F0, 0x323232);
    public static Color ButtonBorder => Pick(0xD3D3D3, 0x454545);
    public static Color Track => Pick(0xD9D9D9, 0x454545);
    public static Color Good => Pick(0x0F7B0F, 0x6CCB5F);
    public static Color Bad => Pick(0xC42B1C, 0xFF99A4);
    public static Color Caution => Pick(0x9D5D00, 0xFCE100);
    public static Color BannerBack => Pick(0xE6F0FA, 0x1E3246);
    public static Color BannerText => Pick(0x003E73, 0xCDE6FF);

    public static Color Fore(Tone tone) => tone switch
    {
        Tone.Secondary => Secondary,
        Tone.Good => Good,
        Tone.Bad => Bad,
        Tone.Caution => Caution,
        Tone.Accent => Accent,
        _ => Text,
    };

    // Segoe UI Variable is the Windows 11 UI font; Windows 10 has only Segoe UI. GDI+ silently falls
    // back to Microsoft Sans Serif for a missing family, which the Name check catches.
    public static readonly Font Body = FontOr("Segoe UI Variable Text", "Segoe UI", 10.5f);
    public static readonly Font Small = FontOr("Segoe UI Variable Small", "Segoe UI", 9f);
    public static readonly Font Strong = new("Segoe UI Semibold", 10.5f);
    public static readonly Font Section = new("Segoe UI Semibold", 10.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 20f);
    public static readonly Font AppTitle = new("Segoe UI Semibold", 11f);
    public static readonly Font Icons = FontOr("Segoe Fluent Icons", "Segoe MDL2 Assets", 12f);

    private static Font FontOr(string family, string fallback, float size)
    {
        var font = new Font(family, size);
        if (font.Name == family) return font;
        font.Dispose();
        return new Font(fallback, size);
    }

    /// <summary>Colors the standard controls under <paramref name="root"/> and repaints the custom ones.</summary>
    public static void Apply(Control root)
    {
        switch (root)
        {
            case Form form:
                form.BackColor = Window;
                form.ForeColor = Text;
                TitleBar(form);
                break;
            case TextBox box:
                box.BackColor = Field;
                box.ForeColor = Text;
                if (box.Multiline) NativeTheme(box, "Explorer");
                break;
            case ComboBox combo:
                combo.BackColor = Field;
                combo.ForeColor = Text;
                NativeTheme(combo, "CFD");
                break;
            case LinkLabel link:
                link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = Accent;
                link.DisabledLinkColor = Disabled;
                break;
            case ScrollableControl { AutoScroll: true } scroll:
                NativeTheme(scroll, "Explorer");
                break;
        }
        foreach (Control child in root.Controls) Apply(child);
        root.Invalidate();
    }

    /// <summary>Dark title bar and, on Windows 11, a caption in the window color (no white strip on top).</summary>
    public static void TitleBar(Form form)
    {
        if (!form.IsHandleCreated) return;
        int dark = Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int caption = ColorTranslator.ToWin32(Window);
        _ = DwmSetWindowAttribute(form.Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
    }

    /// <summary>Scroll bars and drop-down lists follow the theme ("DarkMode_Explorer" is what Explorer uses).</summary>
    private static void NativeTheme(Control control, string lightClass)
    {
        void Set() => _ = SetWindowTheme(control.Handle, Dark ? "DarkMode_" + lightClass : lightClass, null);
        if (control.IsHandleCreated) Set();
        else control.HandleCreated += (_, _) => Set();
    }

    /// <summary>Colors for drop-down menus such as "Beispiel einsetzen".</summary>
    public static void Menu(ContextMenuStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };
        menu.BackColor = Card;
        menu.ForeColor = Text;
        menu.Font = Body;
        foreach (ToolStripItem item in menu.Items) item.ForeColor = Text;
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Card;
        public override Color ImageMarginGradientBegin => Card;
        public override Color ImageMarginGradientMiddle => Card;
        public override Color ImageMarginGradientEnd => Card;
        public override Color MenuBorder => ButtonBorder;
        public override Color MenuItemBorder => ItemSelected;
        public override Color MenuItemSelected => ItemSelected;
        public override Color SeparatorDark => Divider;
        public override Color SeparatorLight => Divider;
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);
}

internal enum Tone { Primary, Secondary, Good, Bad, Caution, Accent }

/// <summary>Label whose color is a role (primary, secondary, good …), so a theme switch recolors it.</summary>
internal class ThemedLabel : Label
{
    private Tone _tone;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Tone Tone
    {
        get => _tone;
        set { _tone = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public override Color ForeColor
    {
        get => Theme.Fore(_tone);
        set { }
    }
}

/// <summary>The blue "what to do now" box on top of the first page at first start.</summary>
internal sealed class Banner : Label
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public override Color ForeColor { get => Theme.BannerText; set { } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public override Color BackColor { get => Theme.BannerBack; set { } }
}

/// <summary>
/// A rounded group of settings, like the cards in the Windows 11 settings. Rows stack top to bottom;
/// <see cref="AddRow"/> puts a hairline between them.
/// </summary>
internal sealed class Card : TableLayoutPanel
{
    public Card()
    {
        ColumnCount = 1;
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(1);
        Margin = new Padding(0, 0, 0, 4);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public override Color BackColor { get => Theme.Card; set { } }

    public void AddRow(Control row)
    {
        if (Controls.Count > 0) Add(new Divider());
        Add(row);
    }

    private void Add(Control c)
    {
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(c, 0, Controls.Count);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, LogicalToDeviceUnits(6));
        using var fill = new SolidBrush(Theme.Card);
        using var pen = new Pen(Theme.CardBorder);
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
    }

    private sealed class Divider : Control
    {
        public Divider()
        {
            Height = 1;
            Dock = DockStyle.Fill;
            Margin = new Padding(0);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public override Color BackColor { get => Theme.Divider; set { } }
    }
}

/// <summary>
/// A text box in the Windows 11 style: rounded field, a line at the bottom that turns into the accent
/// color while typing. The text box itself has no border and sits inside.
/// </summary>
internal sealed class FieldFrame : Panel
{
    private readonly Control _inner;
    private readonly bool _multiline;

    public FieldFrame(Control inner, bool multiline = false)
    {
        _inner = inner;
        _multiline = multiline;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Padding = multiline ? new Padding(10, 7, 3, 5) : new Padding(10, 6, 8, 4);
        Margin = new Padding(0, 4, 0, 4);
        Dock = DockStyle.Top;
        if (inner is TextBox box) box.BorderStyle = BorderStyle.None;
        inner.Dock = multiline ? DockStyle.Fill : DockStyle.Top;
        inner.Margin = new Padding(0);
        Controls.Add(inner);
        inner.GotFocus += (_, _) => Invalidate();
        inner.LostFocus += (_, _) => Invalidate();
        if (!multiline) inner.SizeChanged += (_, _) => FitHeight();
        FitHeight();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public override Color BackColor { get => Theme.Field; set { } }

    private void FitHeight()
    {
        if (!_multiline) Height = _inner.Height + Padding.Vertical + 2;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _inner.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = LogicalToDeviceUnits(4);
        using (var path = Theme.Rounded(r, radius))
        using (var fill = new SolidBrush(Theme.Field))
        using (var pen = new Pen(Theme.FieldBorder))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        // Bottom line: thin grey at rest, 2 px accent while the field has focus.
        bool focused = _inner.ContainsFocus;
        float line = focused ? LogicalToDeviceUnits(2) : 1;
        g.SmoothingMode = SmoothingMode.None;
        using var bottom = new SolidBrush(focused ? Theme.Accent : Theme.FieldBottom);
        g.FillRectangle(bottom, radius, Height - line - 1, Width - 2 * radius, line);
    }
}

/// <summary>Button drawn like a Windows 11 button; <see cref="Accent"/> for the main action (Speichern).</summary>
internal sealed class FluentButton : Button
{
    private bool _hover, _pressed, _accent;

    public FluentButton(string text, bool accent = false)
    {
        Text = text;
        _accent = accent;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        AutoSize = true;
        MinimumSize = new Size(96, 32);
        Padding = new Padding(10, 0, 10, 0);
        Margin = new Padding(8, 0, 0, 0);
        Font = Theme.Body;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Accent
    {
        get => _accent;
        set { _accent = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color fill = !Enabled ? Theme.Button
            : _accent ? (_pressed ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent)
            : _pressed ? Theme.ButtonPressed : _hover ? Theme.ButtonHover : Theme.Button;
        using (var path = Theme.Rounded(r, LogicalToDeviceUnits(4)))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
            if (!_accent || !Enabled)
            {
                using var pen = new Pen(Theme.ButtonBorder);
                g.DrawPath(pen, path);
            }
        }
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Theme.Text, LogicalToDeviceUnits(2));
            using var path = Theme.Rounded(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), LogicalToDeviceUnits(4));
            g.DrawPath(ring, path);
        }
        Color text = !Enabled ? Theme.Disabled : _accent ? Theme.AccentText : Theme.Text;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>On/off switch like in the Windows 11 settings, with "Ein"/"Aus" beside it. Still a CheckBox for keyboard and UI Automation.</summary>
internal sealed class ToggleSwitch : CheckBox
{
    private bool _hover;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Size = new Size(88, 28);
        Font = Theme.Body;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = LogicalToDeviceUnits(40), h = LogicalToDeviceUnits(20);
        var track = new RectangleF(1, (Height - h) / 2, w, h);
        using (var path = Theme.Rounded(track, h / 2))
        {
            if (Checked)
            {
                using var fill = new SolidBrush(_hover ? Theme.AccentHover : Theme.Accent);
                g.FillPath(fill, path);
            }
            else
            {
                using var pen = new Pen(Theme.Secondary);
                g.DrawPath(pen, path);
            }
        }
        float knob = LogicalToDeviceUnits(_hover ? 14 : 12);
        float x = Checked ? track.Right - knob - (h - knob) / 2 : track.X + (h - knob) / 2;
        using (var brush = new SolidBrush(Checked ? Theme.AccentText : Theme.Secondary))
            g.FillEllipse(brush, x, track.Y + (h - knob) / 2, knob, knob);
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Theme.Text, LogicalToDeviceUnits(2));
            using var path = Theme.Rounded(RectangleF.Inflate(track, 2.5f, 2.5f), h / 2 + 2.5f);
            g.DrawPath(ring, path);
        }
        var textBox = new Rectangle((int)(track.Right + LogicalToDeviceUnits(12)), 0, Width, Height);
        TextRenderer.DrawText(g, Checked ? "Ein" : "Aus", Font, textBox, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

/// <summary>Round option button in the Windows 11 style (accent ring with a dot when chosen).</summary>
internal sealed class OptionButton : RadioButton
{
    private bool _hover;

    public OptionButton(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Font = Theme.Strong;
        Margin = new Padding(0);
        Dock = DockStyle.Top;
        Height = 26;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float d = LogicalToDeviceUnits(20);
        var circle = new RectangleF(1, (Height - d) / 2, d, d);
        if (Checked)
        {
            using var fill = new SolidBrush(Theme.Accent);
            g.FillEllipse(fill, circle);
            float dot = LogicalToDeviceUnits(_hover ? 10 : 8);
            using var inner = new SolidBrush(Theme.AccentText);
            g.FillEllipse(inner, circle.X + (d - dot) / 2, circle.Y + (d - dot) / 2, dot, dot);
        }
        else
        {
            using var fill = new SolidBrush(Theme.Field);
            using var pen = new Pen(_hover ? Theme.Text : Theme.Secondary);
            g.FillEllipse(fill, circle);
            g.DrawEllipse(pen, circle);
        }
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Theme.Text, LogicalToDeviceUnits(2));
            g.DrawEllipse(ring, RectangleF.Inflate(circle, 2.5f, 2.5f));
        }
        var text = new Rectangle((int)(circle.Right + LogicalToDeviceUnits(12)), 0, Width, Height);
        TextRenderer.DrawText(g, Text, Font, text, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

/// <summary>Drop-down list drawn in the theme colors (the closed box and the open list).</summary>
internal sealed class FluentCombo : ComboBox
{
    public FluentCombo()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        Font = Theme.Body;
        Margin = new Padding(0, 4, 0, 4);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 32 px closed height like the buttons; ItemHeight is in device pixels and not scaled by WinForms.
        ItemHeight = LogicalToDeviceUnits(26);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool selected = !edit && (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Theme.ItemSelected : Theme.Field))
            e.Graphics.FillRectangle(back, e.Bounds);
        var text = Rectangle.Inflate(e.Bounds, -LogicalToDeviceUnits(6), 0);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, text, Enabled ? Theme.Text : Theme.Disabled,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if (edit && Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, text, Theme.Text, Theme.Field);
    }
}

/// <summary>A page entry on the left, drawn like the Windows 11 settings (icon, pill, accent bar when selected).</summary>
internal sealed class NavItem : Button
{
    private readonly string _icon;
    private bool _selected, _hover;

    public NavItem(string text, string icon)
    {
        Text = text;
        _icon = icon;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Body;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        AccessibleRole = AccessibleRole.PageTab;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; AccessibleDescription = value ? "ausgewählt" : null; Invalidate(); }
    }

    /// <summary>Raised on mouse press (like a tab, so a click that loses the window's activation
    /// halfway still switches) and on keyboard or UI Automation clicks.</summary>
    public event EventHandler? Picked;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) Picked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        // A mouse click already raised Picked on press.
        if (MouseButtons == MouseButtons.None) Picked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Window);
        var r = new RectangleF(0.5f, 2.5f, Width - 1, Height - 5);
        if (_selected || _hover)
        {
            using var fill = new SolidBrush(_selected ? Theme.NavSelected : Theme.NavHover);
            using var path = Theme.Rounded(r, LogicalToDeviceUnits(4));
            g.FillPath(fill, path);
        }
        if (_selected)
        {
            float barH = LogicalToDeviceUnits(16), barW = LogicalToDeviceUnits(3);
            using var bar = new SolidBrush(Theme.Accent);
            using var path = Theme.Rounded(new RectangleF(r.X + 1, (Height - barH) / 2, barW, barH), barW / 2);
            g.FillPath(bar, path);
        }
        int left = LogicalToDeviceUnits(14);
        TextRenderer.DrawText(g, _icon, Theme.Icons, new Rectangle(left, 0, LogicalToDeviceUnits(20), Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        int textLeft = left + LogicalToDeviceUnits(32);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(textLeft, 0, Width - textLeft - LogicalToDeviceUnits(4), Height),
            Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Theme.Text, LogicalToDeviceUnits(2));
            using var path = Theme.Rounded(RectangleF.Inflate(r, -1, -1), LogicalToDeviceUnits(4));
            g.DrawPath(ring, path);
        }
    }
}

/// <summary>Thin rounded bar: microphone level (<see cref="Good"/> green) or download progress (accent).</summary>
internal sealed class Bar : Control
{
    private readonly bool _level;
    private float _shown;

    /// <param name="level">Level meter: quick rise and slow fall. Otherwise a plain 0-100 % progress bar.</param>
    public Bar(bool level)
    {
        _level = level;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = level ? 6 : 4;
        Dock = DockStyle.Top;
    }

    /// <summary>0..1.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Value
    {
        get => _shown;
        set
        {
            if (_level)
            {
                // Speech peaks are short; falling slowly keeps the bar readable.
                float v = Math.Clamp(value * 2.5f, 0, 1);
                _shown = v > _shown ? v : Math.Max(v, _shown - 0.04f);
            }
            else _shown = Math.Clamp(value, 0, 1);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        var track = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var back = new SolidBrush(Theme.Track))
        using (var path = Theme.Rounded(track, Height / 2f))
            g.FillPath(back, path);
        if (_shown <= 0.01f) return;
        var fill = new RectangleF(0, 0, Math.Max(Height, (Width - 1) * _shown), Height - 1);
        using var brush = new SolidBrush(_level ? Theme.Good : Theme.Accent);
        using var fillPath = Theme.Rounded(fill, Height / 2f);
        g.FillPath(brush, fillPath);
    }
}
