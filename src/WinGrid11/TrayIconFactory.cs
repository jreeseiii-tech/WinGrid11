using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WinGrid11;

/// <summary>
/// Renders the tray icon at runtime instead of shipping a static .ico
/// file. Two reasons:
///  * Keeps the repo binary-free.
///  * The icon scales at the rendering size, so a future "Win11 high
///    DPI tray" pass just needs to render at a different size, not
///    add new .ico variants.
///
/// Output is a "WG" / "11" stack on a rounded brand-blue square,
/// matching the Settings window accent (#4080FF).
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Create(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // Rounded brand-blue square. Inset by half a pixel so the
            // anti-aliased edge doesn't clip on the right/bottom.
            using (var path = RoundedRect(0.5f, 0.5f, size - 1f, size - 1f, size * 0.22f))
            using (var fill = new SolidBrush(Color.FromArgb(255, 0x40, 0x80, 0xFF)))
            using (var stroke = new Pen(Color.FromArgb(255, 0x5A, 0x95, 0xFF), 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(stroke, path);
            }

            // Two-line "WG" / "11" stack reads better at 16x16 (the
            // typical tray render size) than four characters on one
            // line. Sized as a fraction of the bitmap so this stays
            // sharp at any rendering size.
            float fontSize = size * 0.36f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            var topRect = new RectangleF(0, size * 0.05f, size, size * 0.5f);
            var bottomRect = new RectangleF(0, size * 0.45f, size, size * 0.5f);
            g.DrawString("WG", font, textBrush, topRect, fmt);
            g.DrawString("11", font, textBrush, bottomRect, fmt);
        }

        // Bitmap.GetHicon allocates an unmanaged HICON. Icon.FromHandle
        // wraps it without taking ownership, so we clone into a managed
        // Icon (which manages its own GDI resource) and destroy the
        // raw handle here to avoid leaking it.
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var wrapped = Icon.FromHandle(hIcon);
            return (Icon)wrapped.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
