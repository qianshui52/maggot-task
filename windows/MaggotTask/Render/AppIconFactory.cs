using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using MaggotTask.Core;

namespace MaggotTask.Render;

/// <summary>
/// 程序化生成应用图标（`--appicon=&lt;path.ico&gt;`，可选 `--preview=&lt;dir&gt;` 导出各尺寸 PNG 供人眼复核）。
///
/// 设计意图（延续设计系统 §5 的"标本缸"语言）：
///   · 深色圆角缸体 —— 浅色/深色任务栏上都不糊（与生物绘制同一条硬约束）；
///   · 缸内一只苍蝇：薄荷色轮廓 + 半透明 V 字翅；
///   · **红元素 = 复眼**（设计系统里的 RotHigh / 危险色），也是 16px 下唯一还看得清的东西，
///     所以它在小尺寸下被刻意放大、加亮。
///   · ≥48px 时沿缸底叠一道红色"腐化弧"，把"腐烂进度"这层含义带进图标。
///
/// 尺寸分级：Windows 会在不同位置取不同尺寸（任务栏 32、Alt-Tab 48、资源管理器 16/256），
/// 所以每个尺寸都是**单独绘制**的，而不是缩放同一张图 —— 16px 下必须省掉腿、翅脉、弧线。
/// </summary>
public static class AppIconFactory
{
    /// <summary>要写进 .ico 的全部尺寸。≤64 用 DIB（兼容性最好），≥128 用 PNG。</summary>
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    // 设计空间 256×256；每个尺寸按 size/256 缩放绘制。
    private const double Unit = 256.0;

    /// <summary>写多尺寸 .ico；[previewDir] 非空时同时导出 PNG 预览。</summary>
    public static string Write(string icoPath, string? previewDir = null)
    {
        List<(int Size, byte[] Payload, bool IsPng)> frames = new();

        foreach (int size in Sizes)
        {
            (BitmapSource bmp, byte[] bgra, int stride) = RenderFrame(size);
            byte[] payload = size >= 128 ? EncodePng(bmp) : EncodeDib(size, bgra, stride);
            frames.Add((size, payload, size >= 128));

            if (!string.IsNullOrWhiteSpace(previewDir))
            {
                Directory.CreateDirectory(previewDir);
                File.WriteAllBytes(Path.Combine(previewDir, $"icon-{size}.png"), EncodePng(bmp));
            }
        }

        WriteIco(icoPath, frames);
        return $"图标已写出：{Path.GetFullPath(icoPath)}（{Sizes.Length} 个尺寸：{string.Join("/", Sizes)}）";
    }

    // ==================================================================
    // 绘制
    // ==================================================================

    private static (BitmapSource, byte[], int) RenderFrame(int size)
    {
        DrawingVisual dv = new();
        using (DrawingContext dc = dv.RenderOpen())
        {
            double k = size / Unit;
            dc.PushTransform(new ScaleTransform(k, k));
            DrawIcon(dc, size);
            dc.Pop();
        }

        // ⚠️ RenderTargetBitmap 只接受 **Pbgra32**（预乘 alpha）等格式，不接受 Bgra32 ——
        // 所以这里按 Pbgra32 渲染，写 DIB 之前再反预乘成直通 alpha。
        RenderTargetBitmap rtb = new(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();

        int stride = size * 4;
        byte[] premultiplied = new byte[stride * size];
        rtb.CopyPixels(premultiplied, stride, 0);
        return (rtb, Unpremultiply(premultiplied), stride);
    }

    /// <summary>预乘 BGRA → 直通 BGRA（ICO 的 DIB 位图要求直通 alpha）。</summary>
    private static byte[] Unpremultiply(byte[] premultiplied)
    {
        byte[] straight = new byte[premultiplied.Length];
        for (int i = 0; i + 3 < premultiplied.Length; i += 4)
        {
            byte a = premultiplied[i + 3];
            if (a == 0 || a == 255)
            {
                straight[i] = premultiplied[i];
                straight[i + 1] = premultiplied[i + 1];
                straight[i + 2] = premultiplied[i + 2];
                straight[i + 3] = a;
                continue;
            }
            straight[i] = (byte)Math.Min(255, premultiplied[i] * 255 / a);
            straight[i + 1] = (byte)Math.Min(255, premultiplied[i + 1] * 255 / a);
            straight[i + 2] = (byte)Math.Min(255, premultiplied[i + 2] * 255 / a);
            straight[i + 3] = a;
        }
        return straight;
    }

    /// <summary>整个图标都在 256×256 设计空间里描述；[size] 只用来决定细节档位。</summary>
    private static void DrawIcon(DrawingContext dc, int size)
    {
        bool tiny = size <= 24;        // 16/20/24：只留轮廓 + 红眼
        bool medium = size is >= 32 and <= 48;
        bool big = size >= 64;

        // ── 缸体：深色圆角方，外侧一道更深的描边保证在任何背景上都"抠得出来"
        Rect jar = new(6, 6, Unit - 12, Unit - 12);
        double radius = 58;
        dc.DrawRoundedRectangle(
            Palette.Frozen(new LinearGradientBrush(
                Color.FromRgb(0x1C, 0x24, 0x1A),
                Color.FromRgb(0x09, 0x0C, 0x07),
                new Point(0.22, 0.0), new Point(0.85, 1.0))),
            new Pen(Palette.Frozen(new SolidColorBrush(Color.FromRgb(0x33, 0x42, 0x2C))), 5),
            jar, radius, radius);

        // 顶部内高光：让缸体像玻璃而不是一块色块
        dc.DrawRoundedRectangle(
            Palette.Frozen(new LinearGradientBrush(
                Color.FromArgb(46, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                new Point(0.5, 0.0), new Point(0.5, 0.55))),
            null,
            new Rect(18, 16, Unit - 36, 96), 34, 34);

        // ── 腐化弧（仅大尺寸）：沿缸体下缘的一道红弧，示意"腐烂进度"
        if (big)
        {
            Arc(dc, new Point(Unit / 2, Unit / 2 + 6), 92,
                new Pen(Palette.Frozen(new SolidColorBrush(Color.FromRgb(0xC4, 0x38, 0x2A))), 9));
        }

        // 构图基准（设计空间 256）：头 196 / 胸 152 / 腹 64~140，居中 y=128。
        // 早期版本腹部画到 96 长、翅只露一片（另一片被腹部整个盖住），
        // 结果读起来是"带一只翅膀的椭圆"——现在把腹部收紧、两片翅都留在体外。
        double cy = Unit / 2;
        Point headC = new(196, cy - 2);
        Point thoraxC = new(152, cy);
        Point abdomenC = new(104, cy);

        // ── 腿：先画（压在身体下面才像"长出来的"）。16px 下省略。
        if (!tiny)
        {
            Pen legPen = new(Palette.Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x14))), 4.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            double[] bx = { 166, 152, 138 };
            double[] ang = { 58, 96, 128 };
            for (int leg = 0; leg < 3; leg++)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    double a1 = side * ang[leg] * Math.PI / 180.0;
                    double a2 = a1 + side * 52.0 * Math.PI / 180.0;
                    Point p = new(bx[leg], cy + side * 24);
                    Point knee = new(p.X + Math.Cos(a1) * 28, p.Y + Math.Sin(a1) * 28);
                    Point ankle = new(knee.X + Math.Cos(a2) * 28, knee.Y + Math.Sin(a2) * 28);
                    dc.DrawLine(legPen, p, knee);
                    dc.DrawLine(legPen, knee, ankle);
                }
            }
        }

        // ── 翅：两片对称，绕翅根向后外张成 V（128°，与实机画法一致）。
        // 画在身体**之前**：内侧被胸部盖住、外侧完整露出，两侧都能看见。
        Point root = new(152, cy - 12);
        for (int side = -1; side <= 1; side += 2)
        {
            dc.PushTransform(new RotateTransform(side * 128.0, root.X, root.Y));
            dc.PushTransform(new TranslateTransform(root.X, root.Y));

            const double wl = 86, ww = 34;   // 翅长 / 翅宽
            dc.DrawEllipse(
                Palette.Frozen(new LinearGradientBrush(
                    Color.FromArgb(tiny ? (byte)150 : (byte)120, 0xE6, 0xF2, 0xEE),
                    Color.FromArgb(175, 0x9F, 0xE0, 0xCB),
                    new Point(0, 0), new Point(1, 0.6))),
                tiny
                    ? null
                    : new Pen(Palette.Frozen(new SolidColorBrush(Color.FromArgb(205, 0x8F, 0xD3, 0xC0))), 4),
                new Point(wl * 0.5, 0), wl * 0.5, ww * 0.5);

            dc.Pop(); // translate
            dc.Pop(); // rotate
        }

        // ── 腹部：收紧的水滴（长 76、半高 30），尾端偏暖表示"在烂"
        StreamGeometry abdomen = new();
        using (StreamGeometryContext g = abdomen.Open())
        {
            g.BeginFigure(new Point(142, cy - 30), true, true);
            g.BezierTo(new Point(74, cy - 34), new Point(50, cy - 16), new Point(44, cy), true, false);
            g.BezierTo(new Point(50, cy + 16), new Point(74, cy + 34), new Point(142, cy + 30), true, false);
        }
        abdomen.Freeze();
        dc.DrawGeometry(
            Palette.Frozen(new LinearGradientBrush(
                Color.FromRgb(0x5C, 0x46, 0x30),   // 尾端偏暖：这是"在腐烂"的那一头
                Color.FromRgb(0x35, 0x42, 0x3A),
                new Point(0, 0.5), new Point(1, 0.5))),
            new Pen(Palette.Frozen(new SolidColorBrush(Color.FromArgb(235, 0x8F, 0xD3, 0xC0))), 5),
            abdomen);
        _ = abdomenC;

        // ── 胸部（比腹部亮，做出体积）
        dc.DrawEllipse(
            Palette.Frozen(new RadialGradientBrush(
                Color.FromRgb(0x50, 0x5F, 0x52),
                Color.FromRgb(0x25, 0x2E, 0x26))
            {
                GradientOrigin = new Point(0.35, 0.3),
                Center = new Point(0.35, 0.3),
                RadiusX = 0.92,
                RadiusY = 0.92,
            }),
            new Pen(Palette.Frozen(new SolidColorBrush(Color.FromArgb(235, 0x8F, 0xD3, 0xC0))), 5),
            thoraxC, 36, 32);

        // ── 头（略放大：头 + 复眼要能读成一个"头"，而不是"两颗红豆"）
        dc.DrawEllipse(
            Palette.Frozen(new SolidColorBrush(Color.FromRgb(0x38, 0x45, 0x39))),
            new Pen(Palette.Frozen(new SolidColorBrush(Color.FromArgb(235, 0x8F, 0xD3, 0xC0))), 5),
            headC, 25, 24);

        // ── 红元素：复眼。小尺寸下放大提亮，保证 16px 也认得出"红眼睛的虫"
        double eyeScale = tiny ? 1.34 : medium ? 1.10 : 1.0;
        double eRx = 21 * eyeScale, eRy = 15 * eyeScale;
        for (int side = -1; side <= 1; side += 2)
        {
            dc.PushTransform(new RotateTransform(side * 16, headC.X, headC.Y));
            Point eye = new(headC.X + 6, headC.Y + side * 13);
            dc.DrawEllipse(
                Palette.Frozen(new RadialGradientBrush(
                    Color.FromRgb(0xF2, 0x6E, 0x4C),
                    Color.FromRgb(0x9C, 0x20, 0x14))
                {
                    GradientOrigin = new Point(0.34, 0.28),
                    Center = new Point(0.34, 0.28),
                    RadiusX = 0.95,
                    RadiusY = 0.95,
                }),
                new Pen(Palette.Frozen(new SolidColorBrush(Color.FromArgb(225, 0x46, 0x0A, 0x04))), 3.5),
                eye, eRx, eRy);
            if (!tiny)
            {
                dc.DrawEllipse(
                    Palette.Frozen(new SolidColorBrush(Color.FromArgb(195, 255, 255, 255))),
                    null,
                    new Point(eye.X - eRx * 0.26, eye.Y - side * eRy * 0.30),
                    eRx * 0.20, eRy * 0.22);
            }
            dc.Pop();
        }
    }

    /// <summary>贴缸体下缘画一段红弧（折线近似，34°→112°）。</summary>
    private static void Arc(DrawingContext dc, Point center, double radius, Pen pen)
    {
        StreamGeometry geo = new();
        using (StreamGeometryContext g = geo.Open())
        {
            bool started = false;
            for (double d = 34; d <= 112; d += 3)
            {
                double rad = d * Math.PI / 180.0;
                Point p = new(center.X + Math.Cos(rad) * radius, center.Y + Math.Sin(rad) * radius);
                if (!started)
                {
                    g.BeginFigure(p, false, false);
                    started = true;
                }
                else
                {
                    g.LineTo(p, true, true);
                }
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    // ==================================================================
    // 编码
    // ==================================================================

    private static byte[] EncodePng(BitmapSource bmp)
    {
        PngBitmapEncoder enc = new();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using MemoryStream ms = new();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>32bpp DIB（BITMAPINFOHEADER + 自下而上的 BGRA + 1bpp AND 掩码）。</summary>
    private static byte[] EncodeDib(int size, byte[] bgra, int stride)
    {
        int maskStride = ((size + 31) / 32) * 4;        // 1bpp 行按 4 字节对齐
        int xorSize = stride * size;
        int andSize = maskStride * size;
        byte[] header = new byte[40 + xorSize + andSize];

        void W32(int off, int v) => BitConverter.GetBytes(v).CopyTo(header, off);

        W32(0, 40);                 // biSize
        W32(4, size);               // biWidth
        W32(8, size * 2);           // biHeight = XOR + AND
        BitConverter.GetBytes((short)1).CopyTo(header, 12);   // biPlanes
        BitConverter.GetBytes((short)32).CopyTo(header, 14);  // biBitCount
        W32(16, 0);                 // biCompression = BI_RGB
        W32(20, xorSize);           // biSizeImage

        // XOR 位图：DIB 是自下而上
        for (int y = 0; y < size; y++)
        {
            Buffer.BlockCopy(bgra, (size - 1 - y) * stride, header, 40 + y * stride, stride);
        }
        // AND 掩码全 0（不透明由 alpha 决定），保持全零即可
        return header;
    }

    // ==================================================================
    // 运行时取用（窗口图标 / 托盘图标）
    //
    // 同一份 Assets\maggot.ico 以 WPF 资源形式嵌入程序集，运行期直接读，
    // 避免"exe 图标一套、窗口/托盘又各画一套"的漂移。
    // ==================================================================

    private const string ResourceUri = "Assets/maggot.ico";

    /// <summary>取最大帧作为窗口图标（任务栏 / Alt-Tab）。失败返回 null，调用方忽略即可。</summary>
    public static ImageSource? LoadImageSource()
    {
        try
        {
            StreamResourceInfo? info = Application.GetResourceStream(new Uri(ResourceUri, UriKind.Relative));
            if (info is null) return null;

            IconBitmapDecoder dec = new(
                info.Stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            BitmapFrame? best = dec.Frames
                .OrderByDescending(f => f.PixelWidth)
                .FirstOrDefault();
            best?.Freeze();
            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>托盘图标（指定尺寸帧；取不到就退回程序化绘制的旧图标）。</summary>
    public static System.Drawing.Icon? LoadTrayIcon(int size)
    {
        try
        {
            StreamResourceInfo? info = Application.GetResourceStream(new Uri(ResourceUri, UriKind.Relative));
            if (info is null) return null;
            return new System.Drawing.Icon(info.Stream, size, size);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteIco(string path, List<(int Size, byte[] Payload, bool IsPng)> frames)    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using FileStream fs = File.Create(path);
        using BinaryWriter w = new(fs);

        w.Write((short)0);                    // reserved
        w.Write((short)1);                    // type = icon
        w.Write((short)frames.Count);

        int offset = 6 + 16 * frames.Count;
        foreach ((int size, byte[] payload, bool _) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));  // 宽（256 记作 0）
            w.Write((byte)(size >= 256 ? 0 : size));  // 高
            w.Write((byte)0);                         // 调色板数
            w.Write((byte)0);                         // reserved
            w.Write((short)1);                        // color planes
            w.Write((short)32);                       // bits per pixel
            w.Write(payload.Length);
            w.Write(offset);
            offset += payload.Length;
        }
        foreach ((int _, byte[] payload, bool __) in frames)
        {
            w.Write(payload);
        }
    }
}
