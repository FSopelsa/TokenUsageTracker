using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TokenUsageTracker;

/// <summary>Draws the remaining percentage as a small icon for the notification area and the taskbar overlay.</summary>
internal sealed class TrayBadge : IDisposable
{
    private readonly IntPtr handle;
    public Icon Icon { get; }
    public ImageSource Image { get; }

    private TrayBadge(IntPtr handle)
    {
        this.handle = handle;
        Icon = Icon.FromHandle(handle);
        var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze();
        Image = image;
    }

    /// <summary>
    /// Codex, and either tool on its own, uses a rounded square in the warning colours. In Both mode Claude
    /// uses a circle in its own shades, because Windows does not let apps choose the order of tray icons.
    /// </summary>
    public static TrayBadge Render(string text, UsageLevel level, int size, string provider = "Codex", bool both = false)
    {
        size = Math.Max(16, size);
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(System.Drawing.Color.Transparent);
            // Same colours as the widget's bars.
            var rgb = UsageColors.Bar(level, provider, both);
            var fill = System.Drawing.Color.FromArgb(rgb.R, rgb.G, rgb.B);
            using (var path = RoundedSquare(size, both && provider == "Claude" ? size / 2f : size / 4f))
            using (var brush = new SolidBrush(fill))
                graphics.FillPath(brush, path);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap
            };
            // Shrink the font until the number fits; "100" needs a smaller size than two digits.
            float em = size * 0.8f;
            var font = new Font("Segoe UI", em, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            while (em > 5 && graphics.MeasureString(text, font, PointF.Empty, format).Width > size - 1)
            {
                font.Dispose();
                em -= 0.5f;
                font = new Font("Segoe UI", em, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            }
            using (font)
            using (var ink = new SolidBrush(System.Drawing.Color.FromArgb(23, 30, 38)))
                graphics.DrawString(text, font, ink, new RectangleF(0, 0, size, size), format);
        }
        return new TrayBadge(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedSquare(float size, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(size - d, 0, d, d, 270, 90);
        path.AddArc(size - d, size - d, d, d, 0, 90);
        path.AddArc(0, size - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Icon.FromHandle does not own the handle, so release it here after the icon is no longer shown.</summary>
    public void Dispose()
    {
        Icon.Dispose();
        WindowsShell.DestroyIcon(handle);
    }
}
