using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WhisperShortcut;

/// <summary>
/// The Screenshot hotkey's area picker, like the Mac's <c>screencapture -i</c>: the screens are captured at once and
/// shown frozen and dimmed; drag a rectangle to take that area, a click without dragging takes the whole screen under
/// it (Mac 8.6x), Esc or right click cancels. WHY our own and not <c>ms-screenclip:</c>: Windows' snip UI needs 1–2 s
/// to appear, the Mac's is instant.
/// </summary>
internal sealed class SnipOverlay : Form
{
    private readonly Bitmap _shot;
    private readonly TaskCompletionSource<Bitmap?> _result;
    private Point? _down;
    private Rectangle _selection;

    private SnipOverlay(Screen screen, Bitmap shot, TaskCompletionSource<Bitmap?> result)
    {
        _shot = shot;
        _result = result;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        TopMost = true; // this one should take the focus: Esc must reach it
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        AccessibleName = "Bildschirmfoto: Bereich aufziehen, klicken für den ganzen Bildschirm, Esc bricht ab";
    }

    /// <summary>Captures every screen, lets the user pick, and returns the picked area as PNG (null = cancelled).</summary>
    public static async Task<byte[]?> PickAsync(CancellationToken ct)
    {
        var previous = NativeMethods.GetForegroundWindow();
        var result = new TaskCompletionSource<Bitmap?>();
        var overlays = new List<SnipOverlay>();
        try
        {
            // All screens first, then the windows: nothing of ours may appear in the picture.
            var shots = Screen.AllScreens.Select(s => (Screen: s, Shot: Grab(s.Bounds))).ToList();
            foreach (var (screen, shot) in shots)
            {
                var overlay = new SnipOverlay(screen, shot, result);
                overlays.Add(overlay);
                overlay.Show();
            }
            var under = overlays.FirstOrDefault(o => o.Bounds.Contains(Cursor.Position)) ?? overlays[0];
            under.Activate();
            using var reg = ct.Register(() => result.TrySetCanceled());
            using var picked = await result.Task;
            if (picked == null) return null;
            using var output = new MemoryStream();
            picked.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        finally
        {
            foreach (var o in overlays) { o.Close(); o._shot.Dispose(); o.Dispose(); }
            // The user's window gets the focus back (the chat closes when it loses it; Dictate Prompt pastes there).
            if (previous != IntPtr.Zero) NativeMethods.SetForegroundWindow(previous);
        }
    }

    private static Bitmap Grab(Rectangle bounds)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return bmp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(_shot, 0, 0);
        using var dim = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
        using var outside = new Region(ClientRectangle);
        if (_selection.Width > 0 && _selection.Height > 0) outside.Exclude(_selection);
        g.FillRegion(dim, outside);
        if (_selection.Width > 0 && _selection.Height > 0)
        {
            using var pen = new Pen(Color.White, 1) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, _selection.X, _selection.Y, _selection.Width - 1, _selection.Height - 1);
            var label = $"{_selection.Width} × {_selection.Height}";
            var size = g.MeasureString(label, Font);
            var at = new PointF(_selection.X, Math.Max(0, _selection.Y - size.Height - 4));
            using var back = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
            g.FillRectangle(back, at.X, at.Y, size.Width + 6, size.Height + 2);
            g.DrawString(label, Font, Brushes.White, at.X + 3, at.Y + 1);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Finish(null); return; }
        if (e.Button != MouseButtons.Left) return;
        _down = e.Location;
        _selection = Rectangle.Empty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_down is not { } d) return;
        var old = _selection;
        _selection = Rectangle.FromLTRB(Math.Min(d.X, e.X), Math.Min(d.Y, e.Y), Math.Max(d.X, e.X) + 1, Math.Max(d.Y, e.Y) + 1);
        Invalidate(Rectangle.Inflate(Rectangle.Union(old, _selection), 2, 40));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _down is not { } d) return;
        _down = null;
        // Mac: a click (moved < 5 pt) takes the whole screen under it.
        bool click = Math.Abs(e.X - d.X) < 5 && Math.Abs(e.Y - d.Y) < 5;
        var area = click ? new Rectangle(Point.Empty, _shot.Size) : Rectangle.Intersect(_selection, new Rectangle(Point.Empty, _shot.Size));
        if (area.Width < 1 || area.Height < 1) { Finish(null); return; }
        Finish(_shot.Clone(area, _shot.PixelFormat));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Finish(null);
        else if (e.KeyCode == Keys.Enter) Finish(_shot.Clone(new Rectangle(Point.Empty, _shot.Size), _shot.PixelFormat));
    }

    private void Finish(Bitmap? picked)
    {
        if (!_result.TrySetResult(picked)) picked?.Dispose();
    }
}
