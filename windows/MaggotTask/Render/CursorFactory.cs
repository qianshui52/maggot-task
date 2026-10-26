using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaggotTask.Core;

namespace MaggotTask.Render;

/// <summary>
/// 程序化生成工具光标（.cur 字节流，运行时构造 <see cref="Cursor"/>）。
///
/// 为什么不用图片资源：本项目的硬约束是「零位图素材」——
/// 应用图标走 <see cref="AppIconFactory"/>，光标同理，全部用矢量现画。
///
/// CUR 与 ICO 的差别只有两处：
///   · ICONDIR.type = 2（而不是 1）
///   · ICONDIRENTRY 的 wPlanes / wBitCount 两个 WORD 被复用为 hotspotX / hotspotY
/// 其余（BITMAPINFOHEADER + 自下而上 BGRA + 1bpp AND 掩码）完全一致。
/// </summary>
public static class CursorFactory
{
    /// <summary>光标位图边长（像素）。40px 在 125% 缩放下约等于 32 逻辑像素，大小合适。</summary>
    private const int Size = 40;

    private static readonly Dictionary<string, Cursor?> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>取某件工具的光标；生成失败返回 null（调用方退回系统箭头）。</summary>
    public static Cursor? ForTool(string? toolId)
    {
        string id = ToolCatalog.Get(toolId).Id;
        lock (Gate)
        {
            if (Cache.TryGetValue(id, out Cursor? cached)) return cached;

            Cursor? cursor = null;
            try
            {
                cursor = Build(id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MaggotTask] 生成光标失败({id}): {ex.Message}");
            }
            Cache[id] = cursor;
            return cursor;
        }
    }

    /// <summary>清空缓存（诊断时换 DPI 重新生成）。</summary>
    public static void Reset()
    {
        lock (Gate) Cache.Clear();
    }

    // ------------------------------------------------------------------
    // 构建
    // ------------------------------------------------------------------

    private static Cursor Build(string toolId)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            switch (toolId)
            {
                case ToolCatalog.SprayId: DrawSpray(dc); break;
                case ToolCatalog.KatanaId: DrawKatana(dc); break;
                case ToolCatalog.LaserId: DrawLaser(dc); break;
                default: DrawSwatter(dc); break;
            }
        }

        var bmp = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        int stride = Size * 4;
        byte[] pixels = new byte[stride * Size];
        bmp.CopyPixels(pixels, stride, 0);
        Unpremultiply(pixels);

        (int hx, int hy) = Hotspot(toolId);
        byte[] cur = WriteCur(pixels, Size, Size, hx, hy);
        return new Cursor(new MemoryStream(cur));
    }

    /// <summary>热点：光标"真正指着的那个点"。</summary>
    private static (int X, int Y) Hotspot(string toolId) => toolId switch
    {
        ToolCatalog.SprayId => (28, 15),
        ToolCatalog.KatanaId => (35, 5),
        ToolCatalog.LaserId => (36, 22),
        _ => (27, 12),
    };

    // ------------------------------------------------------------------
    // 四支笔的画法
    // ------------------------------------------------------------------

    private static readonly Brush Dark = Frozen(new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x0A)));
    private static readonly Brush Light = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xE0)));
    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0xCE, 0x00)));
    private static readonly Brush Gold = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x2E)));
    private static readonly Brush Teal = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xA9, 0x8E)));

    private static Pen P(Brush b, double w) => new(b, w)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    /// <summary>苍蝇拍：深色描边 + 浅色拍面 + 十字网纹。</summary>
    private static void DrawSwatter(DrawingContext dc)
    {
        // 柄
        dc.DrawLine(P(Dark, 5.0), new Point(6, 35), new Point(21, 20));
        dc.DrawLine(P(Light, 2.2), new Point(6, 35), new Point(21, 20));

        dc.PushTransform(new RotateTransform(-40, 27, 12));

        // 拍面：先画大一圈的深色当描边
        dc.DrawEllipse(Dark, null, new Point(27, 12), 12.2, 9.6);
        dc.DrawEllipse(Light, null, new Point(27, 12), 10.4, 7.9);

        // 网纹
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(150, 0x2A, 0x36, 0x24)), 1.0);
        for (int i = -2; i <= 2; i++)
        {
            double dy = i * 3.0;
            double dx = Math.Sqrt(Math.Max(0, 1 - (dy * dy) / (7.9 * 7.9))) * 10.4;
            dc.DrawLine(grid, new Point(27 - dx, 12 + dy), new Point(27 + dx, 12 + dy));
        }
        for (int i = -2; i <= 2; i++)
        {
            double dx = i * 3.6;
            double dy = Math.Sqrt(Math.Max(0, 1 - (dx * dx) / (10.4 * 10.4))) * 7.9;
            dc.DrawLine(grid, new Point(27 + dx, 12 - dy), new Point(27 + dx, 12 + dy));
        }

        dc.Pop();
    }

    /// <summary>杀虫喷雾：罐体 + 喷头 + 三点雾滴。</summary>
    private static void DrawSpray(DrawingContext dc)
    {
        // 罐体
        var body = new Rect(6, 14, 14, 22);
        dc.DrawRoundedRectangle(Dark, null, body, 3.5, 3.5);
        dc.DrawRoundedRectangle(Accent, null, new Rect(8, 16, 10, 18), 2.5, 2.5);

        // 顶盖
        dc.DrawRoundedRectangle(Dark, null, new Rect(8.5, 9, 9, 6), 2, 2);
        dc.DrawRoundedRectangle(Accent, null, new Rect(9.8, 10.2, 6.4, 3.6), 1.5, 1.5);

        // 喷头
        dc.DrawGeometry(Dark, null, Poly(
            new Point(17, 11), new Point(27, 13.5), new Point(27, 17.5), new Point(17, 17)));
        dc.DrawGeometry(Light, null, Poly(
            new Point(18.5, 12.4), new Point(25.4, 14.3), new Point(25.4, 16.7), new Point(18.5, 15.6)));

        // 雾滴（向右上散开）
        dc.DrawEllipse(Accent, null, new Point(30, 12), 1.7, 1.7);
        dc.DrawEllipse(Accent, null, new Point(34, 9.5), 1.2, 1.2);
        dc.DrawEllipse(Accent, null, new Point(33, 15), 1.0, 1.0);
    }

    /// <summary>唐太刀：斜置的长刃 + 镡 + 柄。</summary>
    private static void DrawKatana(DrawingContext dc)
    {
        // 刃（尖端在热点）
        Point tip = new(35, 5);
        Point baseA = new(15.5, 22);
        Point baseB = new(19.5, 26);
        dc.DrawGeometry(Dark, null, Poly(tip, baseA, baseB, new Point(33, 7)));
        dc.DrawGeometry(Light, null, Poly(new Point(33.6, 7.6), new Point(17.4, 22.6), new Point(19.2, 24.4)));

        // 镡（护手）
        dc.PushTransform(new RotateTransform(45, 17, 24));
        dc.DrawRoundedRectangle(Dark, null, new Rect(12.5, 20.5, 9, 7), 2, 2);
        dc.DrawRoundedRectangle(Gold, null, new Rect(13.6, 21.6, 6.8, 4.8), 1.5, 1.5);
        dc.Pop();

        // 柄
        dc.DrawLine(P(Dark, 6.0), new Point(15, 27), new Point(5, 37));
        dc.DrawLine(P(Accent, 3.2), new Point(15, 27), new Point(5, 37));
    }

    /// <summary>霓虹激光枪：枪身 + 长管 + 枪口光点。</summary>
    private static void DrawLaser(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Dark, null, new Rect(5, 18, 19, 11), 3, 3);
        dc.DrawRoundedRectangle(Teal, null, new Rect(6.6, 19.6, 15.8, 7.8), 2, 2);

        // 枪管
        dc.DrawRoundedRectangle(Dark, null, new Rect(22, 20.5, 14, 6), 2, 2);
        dc.DrawRoundedRectangle(Light, null, new Rect(23.4, 21.8, 11.2, 3.4), 1.4, 1.4);

        // 握把
        dc.DrawGeometry(Dark, null, Poly(
            new Point(10, 28), new Point(17, 28), new Point(15.5, 37), new Point(9, 37)));
        dc.DrawGeometry(Dark, null, Poly(
            new Point(11.4, 29.4), new Point(15.4, 29.4), new Point(14.2, 35.6), new Point(10.6, 35.6)));

        // 枪口光点
        dc.DrawEllipse(Teal, null, new Point(36, 23.5), 2.6, 2.6);
        dc.DrawEllipse(Frozen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))), null,
            new Point(36, 23.5), 1.1, 1.1);
    }

    private static StreamGeometry Poly(params Point[] points)
    {
        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            for (int i = 1; i < points.Length; i++) ctx.LineTo(points[i], true, false);
        }
        geo.Freeze();
        return geo;
    }

    private static Brush Frozen(Brush b)
    {
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    // ------------------------------------------------------------------
    // 位图 → CUR
    // ------------------------------------------------------------------

    /// <summary>Pbgra32 是预乘 alpha，写 DIB 前必须反预乘，否则边缘发黑。</summary>
    private static void Unpremultiply(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (a == 0 || a == 255) continue;
            bgra[i] = (byte)Math.Min(255, bgra[i] * 255 / a);
            bgra[i + 1] = (byte)Math.Min(255, bgra[i + 1] * 255 / a);
            bgra[i + 2] = (byte)Math.Min(255, bgra[i + 2] * 255 / a);
        }
    }

    /// <summary>写单帧 CUR（32bpp DIB + 1bpp AND 掩码）。</summary>
    private static byte[] WriteCur(byte[] bgraTopDown, int width, int height, int hotspotX, int hotspotY)
    {
        int maskStride = ((width + 31) / 32) * 4;
        int xorSize = width * height * 4;
        int andSize = maskStride * height;
        int imageSize = 40 + xorSize + andSize;

        using var ms = new MemoryStream(6 + 16 + imageSize);
        using var w = new System.IO.BinaryWriter(ms);

        // ICONDIR
        w.Write((ushort)0);      // reserved
        w.Write((ushort)2);      // type = cursor
        w.Write((ushort)1);      // count

        // ICONDIRENTRY（宽度/高度 40 → 直接写字节）
        w.Write((byte)width);
        w.Write((byte)height);
        w.Write((byte)0);                       // colorCount
        w.Write((byte)0);                       // reserved
        w.Write((ushort)hotspotX);              // ← wPlanes 复用为热点 X
        w.Write((ushort)hotspotY);              // ← wBitCount 复用为热点 Y
        w.Write((uint)imageSize);
        w.Write((uint)22);                      // 6 + 16

        // BITMAPINFOHEADER：biHeight 是 XOR + AND 的总高（2×）
        w.Write(40);
        w.Write(width);
        w.Write(height * 2);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(0);                             // BI_RGB
        w.Write(xorSize + andSize);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);

        // XOR：自下而上
        for (int y = height - 1; y >= 0; y--)
        {
            int row = y * width * 4;
            w.Write(bgraTopDown, row, width * 4);
        }

        // AND 掩码：1 = 透明
        for (int y = height - 1; y >= 0; y--)
        {
            byte[] maskRow = new byte[maskStride];
            for (int x = 0; x < width; x++)
            {
                byte a = bgraTopDown[(y * width + x) * 4 + 3];
                if (a < 128) maskRow[x >> 3] |= (byte)(0x80 >> (x & 7));
            }
            w.Write(maskRow);
        }

        w.Flush();
        return ms.ToArray();
    }
}
