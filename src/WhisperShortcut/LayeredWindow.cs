using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WhisperShortcut;

/// <summary>
/// Per-pixel-alpha windows (the recording pill and the notification toast): draw into a premultiplied
/// bitmap, hand it to UpdateLayeredWindow. The form needs WS_EX_LAYERED and does no painting itself.
/// </summary>
internal static class LayeredWindow
{
    public static Bitmap NewBitmap(int width, int height) => new(width, height, PixelFormat.Format32bppPArgb);

    /// <summary>Shows <paramref name="bmp"/> as the window's content at <paramref name="position"/>; <paramref name="alpha"/> fades the whole window.</summary>
    public static void Update(IntPtr hwnd, Bitmap bmp, Point position, byte alpha)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = position.X, y = position.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Soft shadow under a rounded card: stacked translucent rounded rectangles, widest first.</summary>
    public static void Shadow(Graphics g, RectangleF card, float radius, float s)
    {
        const int layers = 16;
        for (int i = 0; i < layers; i++)
        {
            float grow = (layers - i) * 1.15f * s;
            var r = new RectangleF(card.X - grow, card.Y - grow + 5 * s, card.Width + 2 * grow, card.Height + 2 * grow);
            using var path = Theme.Rounded(r, radius + grow);
            using var b = new SolidBrush(Color.FromArgb(4, 0, 0, 0));
            g.FillPath(b, path);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
