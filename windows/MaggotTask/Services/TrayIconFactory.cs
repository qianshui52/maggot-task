using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MaggotTask.Services;

/// <summary>
/// 程序化生成托盘图标（不引用任何位图资源，与设计系统的矢量生物一致）。
/// 标本缸底 + 一只米黄蛆虫 + 深色描边。
///
/// 注意：本文件用的是 **GDI+**（System.Drawing）而不是 WPF 的 Media 类型，
/// 因为 NotifyIcon 需要 System.Drawing.Icon。全局 using 别名把 Pen/Brush/Color
/// 指向了 WPF，所以这里一律写全名。
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>生成一个 32×32 的虫图标。调用方负责 Dispose。</summary>
    public static System.Drawing.Icon Create(int size = 32)
    {
        using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            float s = size / 32f;

            using var voidBrush = new System.Drawing.SolidBrush(Gdi("#0C100A"));
            using var borderPen = new System.Drawing.Pen(Gdi("#2A3624"), 1.4f * s);
            using var larvaBrush = new System.Drawing.SolidBrush(Gdi("#E9D9A3"));
            using var larvaDeepBrush = new System.Drawing.SolidBrush(Gdi("#C4A85F"));
            using var outlinePen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(140, 20, 24, 16), 1.5f * s)
            {
                LineJoin = LineJoin.Round,
            };

            // 标本缸底
            var jar = new System.Drawing.RectangleF(1.2f * s, 1.2f * s, 29.6f * s, 29.6f * s);
            using (GraphicsPath path = RoundedRect(jar, 8f * s))
            {
                g.FillPath(voidBrush, path);
                g.DrawPath(borderPen, path);
            }

            // 蛆：6 节沿正弦脊线
            System.Drawing.PointF[] spine =
            {
                new(7.5f * s, 22.0f * s),
                new(10.6f * s, 19.4f * s),
                new(13.7f * s, 17.4f * s),
                new(16.8f * s, 16.2f * s),
                new(19.9f * s, 16.0f * s),
                new(23.0f * s, 16.8f * s),
            };

            for (int i = 0; i < spine.Length; i++)
            {
                float t = i / (float)(spine.Length - 1);
                float r = (3.5f - 0.8f * t) * s;
                var seg = new System.Drawing.RectangleF(spine[i].X - r, spine[i].Y - r, r * 2, r * 2);
                g.FillEllipse(i == 0 ? larvaDeepBrush : larvaBrush, seg);
                g.DrawEllipse(outlinePen, seg);
            }

            // 头节口钩
            using (var hookPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(190, 20, 24, 16), 1.2f * s))
            {
                g.DrawLine(hookPen, 6.0f * s, 21.2f * s, 7.6f * s, 23.4f * s);
                g.DrawLine(hookPen, 6.6f * s, 20.2f * s, 8.4f * s, 22.0f * s);
            }
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using System.Drawing.Icon temp = System.Drawing.Icon.FromHandle(h);
            return (System.Drawing.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    private static System.Drawing.Color Gdi(string hex)
        => System.Drawing.ColorTranslator.FromHtml(hex);

    private static GraphicsPath RoundedRect(System.Drawing.RectangleF r, float radius)
    {
        float d = radius * 2f;
        GraphicsPath p = new();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
