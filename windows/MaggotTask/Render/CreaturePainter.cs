using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MaggotTask.Core;

namespace MaggotTask.Render;

/// <summary>
/// 程序化矢量生物绘制（设计系统 §5）。不使用任何位图资源。
///
/// 硬约束（设计系统 §0）：每只生物都必须有
///   1) 1.5px 深色描边 rgba(20,24,16,0.55) —— 浅色背景上不消失
///   2) rgba(0,0,0,0.45) blur 6px offset(0,2) 柔和外阴影 —— 杂乱背景上浮起来
///   3) 中间调身体 —— 纯白 / 纯黑背景都能读
/// 绘制顺序：阴影 → 描边 → 本体 → 高光 → 细节。
/// </summary>
public static class CreaturePainter
{
    /// <summary>深色描边宽度（逻辑像素）。</summary>
    public const double OutlineWidth = 1.5;

    /// <summary>阴影模糊半径。</summary>
    public const double ShadowBlur = 6.0;

    /// <summary>阴影偏移（0, 2）。</summary>
    public static readonly Vector ShadowOffset = new(0, 2);

    private static readonly Pen OutlinePen = MakePen(Palette.OutlineBrush, OutlineWidth);
    private static readonly Pen FlyRimPen = MakePen(Palette.Frozen(new SolidColorBrush(Palette.FlyRim)), 1.2);
    private static readonly Pen SegmentPen = MakePen(Palette.LarvaDeepBrush, 1.5);
    private static readonly Pen PupaBandPen = MakePen(Palette.PupaDeepBrush, 1.4);
    private static readonly Pen LegPen = MakePen(Palette.OutlineBrush, 1.3);
    private static readonly Pen MouthHookPen = MakePen(Palette.Frozen(new SolidColorBrush(Color.FromArgb(200, 20, 24, 16))), 1.4);
    private static readonly Brush ShadowBrush = Palette.Frozen(new SolidColorBrush(Palette.Shadow));

    /// <summary>
    /// 阴影渲染方式。
    ///
    /// **这是整个应用最大的性能开关。** 覆盖层是 `AllowsTransparency=True` 的分层窗口，
    /// WPF 对这种窗口强制走**软件渲染**，硬件加速完全用不上。于是给整层阴影挂一个
    /// 全屏高斯模糊，等于每帧在 CPU 上糊一遍 1536×864 —— 实测能把帧率压到十几帧，
    /// 肉眼就是"电脑很卡"。
    ///
    /// 逐只画一个径向渐变椭圆得到几乎相同的柔性阴影，但完全不需要 Effect。
    /// </summary>
    public enum ShadowMode
    {
        /// <summary>每只生物一个径向渐变椭圆，无 Effect（默认，快）。</summary>
        SoftGradient,

        /// <summary>整层套一个全屏 BlurEffect（旧实现，软件渲染下极慢）。</summary>
        BlurLayer,

        /// <summary>完全不画阴影。仅用于诊断，隔离阴影占多少成本。</summary>
        None,

        /// <summary>纯实心椭圆（无渐变、无位图）。诊断用：隔离 DrawImage / 渐变的影响。</summary>
        Solid,
    }

    /// <summary>当前阴影模式（启动时可用 --shadow=blur 切回旧实现做对照）。</summary>
    public static ShadowMode Shadow { get; set; } = ShadowMode.SoftGradient;

    /// <summary>诊断用：当前阴影模式的文字描述。</summary>
    public static string ShadowModeDescription => Shadow switch
    {
        ShadowMode.BlurLayer => "BlurLayer（整层全屏高斯模糊 Effect）",
        ShadowMode.None => "None（不画阴影，仅诊断）",
        _ => "SoftGradient（逐只径向渐变，无 Effect）",
    };

    /// <summary>
    /// 柔性阴影位图：把径向渐变**一次性**烘到一张 128×128 的小图里，
    /// 之后每帧只做 DrawImage（缩放一张小图），不再逐只光栅化渐变。
    ///
    /// 实测（140 只生物，OrayIddDriver 虚拟显示器 / 纯软件渲染）：
    ///   整层 BlurEffect  ≈ 3 fps
    ///   逐只 RadialGradientBrush ≈ 4 fps   ← 软件光栅化渐变极慢，是真正的瓶颈
    ///   不画阴影          = 18.7 fps
    ///   缓存位图          = 见下（接近"不画阴影"）
    ///
    /// 也就是说：**"卡"的根源不是虫子多，而是每只虫子每帧都在重算一遍渐变。**
    /// </summary>
    private static readonly ImageSource SoftShadowImage = CreateSoftShadowImage();

    private const int ShadowTexSize = 128;

    private static ImageSource CreateSoftShadowImage()
    {
        Color c = Palette.Shadow;
        RadialGradientBrush b = new()
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        b.GradientStops.Add(new GradientStop(c, 0.0));
        // 中段维持接近满不透明，边缘才快速淡出 —— 这样才像"投影"，而不是"光晕"。
        b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(c.A * 0.82), c.R, c.G, c.B), 0.55));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1.0));
        b.Freeze();

        DrawingVisual dv = new();
        using (DrawingContext dc = dv.RenderOpen())
        {
            double half = ShadowTexSize / 2.0;
            dc.DrawEllipse(b, null, new Point(half, half), half, half);
        }

        RenderTargetBitmap rtb = new(ShadowTexSize, ShadowTexSize, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// 共用的模糊效果实例（Freeze 后线程安全、可跨 Visual 复用）。
    /// 每帧 new 一个 BlurEffect 会让 WPF 反复重编译 shader，实测帧耗翻倍。
    /// </summary>
    public static readonly BlurEffect SharedShadowEffect = CreateShadowEffectFrozen();

    private static BlurEffect CreateShadowEffectFrozen()
    {
        BlurEffect e = new()
        {
            Radius = ShadowBlur,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance,
        };
        e.Freeze();
        return e;
    }

    private static Pen MakePen(Brush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    // ==================================================================
    // 阴影层
    // ==================================================================

    /// <summary>
    /// 在独立图层里画柔和外阴影。调用方需给该 DrawingVisual 装 <see cref="ShadowEffect"/>，
    /// 并先于本体层渲染，否则模糊会把本体也糊掉。
    /// </summary>
    public static void DrawShadow(DrawingContext dc, CreatureVisual c, double time)
    {
        double a = c.SpawnProgress;
        if (a <= 0.001) return;
        if (Shadow == ShadowMode.None) return;

        // 必须用 c.Size 而不是 c.SizeScale：SizeScale 只是 1.0~1.15 的系数，
        // 早期版本用它去乘一批硬编码尺寸，结果阴影比本体还小，看起来像画错了。
        double s = c.Size;

        // 尺寸比本体略大一圈，这样投影才会从轮廓外缘透出来。
        double w, h;
        bool rotate;
        switch (c.Stage)
        {
            case Stage.Egg:
                // 本体 0.80s × 1.00s
                w = 0.88 * s; h = 1.10 * s; rotate = false; break;
            case Stage.Larva:
                // 本体约 1.38s × 0.43s，沿朝向伸展
                w = 1.45 * s; h = 0.52 * s; rotate = true; break;
            case Stage.Pupa:
                // 本体 0.42s × 0.756s
                w = 0.52 * s; h = 0.86 * s; rotate = false; break;
            default: // Fly
                // 翅向两侧张开后翼展 ≈ 体长（真实蝇就是这样）。阴影取 1.10s 的折中：
                // 完全按翼展(1.40s)会变成一团近乎正圆的大黑斑，只覆盖体干(0.70s)
                // 又会缩在翅膀下面。1.10s 读起来是"虫体+翅"的合成投影。
                w = 1.45 * s; h = 1.10 * s; rotate = true; break;
        }

        // 呼吸/蠕动轻微带动阴影，静态阴影会显得贴纸感过强。
        double wobble = c.Stage is Stage.Egg or Stage.Pupa
            ? 1.0
            : 1.0 + 0.03 * Math.Sin(time * 2.1 + c.Seed);
        double alpha = 0.85 * a;

        var center = new Point(c.Position.X + ShadowOffset.X, c.Position.Y + ShadowOffset.Y);

        // 有朝向的阶段（蛆 / 苍蝇）阴影也要跟着转，否则身体斜着、影子却是横躺的。
        if (rotate)
        {
            dc.PushTransform(new RotateTransform(c.Heading * 180.0 / Math.PI, center.X, center.Y));
        }

        double rx = w * 0.5 * wobble;
        double ry = h * 0.5 * wobble;

        // 只在入场淡入那 0.45 秒里才需要透明度图层。
        //
        // ⚠️ 判据必须是 SpawnProgress(a)，不能是 alpha！alpha = 0.85 * a 含阴影强度
        // 系数，**恒小于 1**，拿它做判断会让每一只、每一帧都开一层合成表面 ——
        // 实测 100 只就是 ~190ms/帧，直接把帧率从 15fps 打到 4fps。
        bool fading = a < 0.999;
        if (fading) dc.PushOpacity(alpha);

        if (Shadow == ShadowMode.SoftGradient)
        {
            // 缓存位图：一次 DrawImage，不再逐帧重算渐变。
            dc.DrawImage(SoftShadowImage, new Rect(center.X - rx, center.Y - ry, rx * 2.0, ry * 2.0));
        }
        else if (Shadow == ShadowMode.Solid)
        {
            dc.DrawEllipse(ShadowBrush, null, center, rx, ry);
        }
        else
        {
            // 旧路径：画实心椭圆，由所在图层的全屏 BlurEffect 负责柔化。
            dc.DrawEllipse(ShadowBrush, null, center, rx, ry);
        }

        if (fading) dc.Pop();
        if (rotate) dc.Pop();
    }

    /// <summary>给阴影层用的模糊效果（新建实例；渲染循环请用 <see cref="SharedShadowEffect"/>）。</summary>
    public static BlurEffect CreateShadowEffect() => new()
    {
        Radius = ShadowBlur,
        KernelType = KernelType.Gaussian,
        RenderingBias = RenderingBias.Performance,
    };

    // ==================================================================
    // 本体层
    // ==================================================================

    /// <summary>按阶段分派绘制。</summary>
    public static void DrawBody(DrawingContext dc, CreatureVisual c, double time, bool reduceMotion)
    {
        double a = c.SpawnProgress;
        if (a <= 0.001) return;

        // 同上：入场淡入才需要图层，稳态下省掉一层合成表面。
        bool fading = a < 0.999;
        if (fading) dc.PushOpacity(Math.Min(1.0, a));
        switch (c.Stage)
        {
            case Stage.Egg:
                DrawEgg(dc, c, time, reduceMotion);
                break;
            case Stage.Larva:
                DrawLarva(dc, c, time, reduceMotion);
                break;
            case Stage.Pupa:
                DrawPupa(dc, c, time, reduceMotion);
                break;
            case Stage.Fly:
                DrawFly(dc, c, time, reduceMotion);
                break;
        }
        if (fading) dc.Pop();
    }

    // ------------------------------------------------------------------
    // 卵 EGG —— 椭圆 1 : 1.25（竖直），乳白，暗斑随 rot
    // ------------------------------------------------------------------
    private static void DrawEgg(DrawingContext dc, CreatureVisual c, double time, bool reduceMotion)
    {
        double s = c.Size;
        double rx = 0.40 * s;
        double ry = 0.40 * s * 1.25;

        // 呼吸：scale = 1 + 0.04 * sin(t*1.6 + seed)；reduced-motion 改为 ≥4s 周期的缓慢呼吸。
        double breath = reduceMotion
            ? 1.0 + 0.03 * Math.Sin(time * (Math.PI * 2.0 / 5.0) + c.Seed)
            : 1.0 + 0.04 * Math.Sin(time * 1.6 + c.Seed);

        double rx2 = rx * breath;
        double ry2 = ry * breath;

        var rng = new Rng(c.Seed);
        // 位置抖动一点点，避免"印刷感"。
        double jx = (rng.NextDouble() - 0.5) * 0.6;
        double jy = (rng.NextDouble() - 0.5) * 0.6;
        Point center = new(c.Position.X + jx, c.Position.Y + jy);

        dc.DrawEllipse(Palette.EggBrush, OutlinePen,
            center, rx2, ry2);

        // 右下 1/3 用 --egg-deep 做体积
        dc.PushClip(new EllipseGeometry(center, rx2, ry2));
        dc.DrawEllipse(Palette.EggDeepBrush, null,
            new Point(center.X + rx2 * 0.34, center.Y + ry2 * 0.36),
            rx2 * 0.78, ry2 * 0.72);
        dc.Pop();

        // 左上高光：白色 55% 透明椭圆
        dc.DrawEllipse(Palette.Highlight55, null,
            new Point(center.X - rx2 * 0.34, center.Y - ry2 * 0.36),
            rx2 * 0.30, ry2 * 0.26);

        // rot > 0.08 起叠加暗斑：floor((rot-0.08)*30) 个 #8A7F5E 小点
        if (c.Rot > 0.08f)
        {
            int spots = (int)Math.Floor((c.Rot - 0.08f) * 30.0);
            spots = Math.Min(spots, 26);
            for (int i = 0; i < spots; i++)
            {
                double ang = rng.NextDouble() * Math.PI * 2.0;
                double rr = Math.Sqrt(rng.NextDouble()) * 0.72;
                double px = center.X + Math.Cos(ang) * rr * rx2;
                double py = center.Y + Math.Sin(ang) * rr * ry2;
                double rad = 0.09 * s * (0.6 + rng.NextDouble() * 0.8);
                dc.DrawEllipse(Palette.EggSpotBrush, null,
                    new Point(px, py), rad, rad);
            }
        }
    }

    // ------------------------------------------------------------------
    // 蛆 LARVA —— 6 节沿正弦脊线，半径 1.0 → 0.72 递减
    // ------------------------------------------------------------------
    private static void DrawLarva(DrawingContext dc, CreatureVisual c, double time, bool reduceMotion)
    {
        const int segments = 6;
        double s = c.Size;
        double heading = c.Heading;
        var dir = new Vector(Math.Cos(heading), Math.Sin(heading));
        var normal = new Vector(-dir.Y, dir.X);

        double amp = reduceMotion ? 0.0 : 0.10 * s;
        double wobbleSpeed = reduceMotion ? (Math.PI * 2.0 / 4.5) : 3.4;
        double phase = time * wobbleSpeed + c.Seed;
        double step = 0.19 * s;

        Point head = c.Position;

        // 1) 本体 + 描边
        Point[] centers = new Point[segments];
        double[] radii = new double[segments];
        for (int i = 0; i < segments; i++)
        {
            double t = i / (double)(segments - 1);
            double lateral = amp * Math.Sin(phase + i * 0.9);
            centers[i] = new Point(
                head.X - dir.X * step * i + normal.X * lateral,
                head.Y - dir.Y * step * i + normal.Y * lateral);
            radii[i] = 0.215 * s * (1.0 - 0.28 * t);
        }

        for (int i = segments - 1; i >= 0; i--)
        {
            Brush fill = i == 0 ? Palette.LarvaDeepBrush : Palette.LarvaBrush;
            dc.DrawEllipse(fill, OutlinePen, centers[i], radii[i], radii[i]);
        }

        // 2) 每节底部叠 --larva-deep 月牙
        for (int i = 1; i < segments; i++)
        {
            var crescent = new Point(
                centers[i].X + normal.X * radii[i] * 0.30 + dir.X * radii[i] * 0.10,
                centers[i].Y + normal.Y * radii[i] * 0.30 + dir.Y * radii[i] * 0.10);
            dc.DrawEllipse(Palette.LarvaDeepBrush, null, crescent, radii[i] * 0.62, radii[i] * 0.58);
        }

        // 3) 分节缝：--larva-deep，宽 1.5，圆头
        for (int i = 0; i < segments - 1; i++)
        {
            Point mid = new(
                (centers[i].X + centers[i + 1].X) * 0.5,
                (centers[i].Y + centers[i + 1].Y) * 0.5);
            double half = radii[i] * 0.74;
            dc.DrawLine(SegmentPen,
                new Point(mid.X - normal.X * half, mid.Y - normal.Y * half),
                new Point(mid.X + normal.X * half, mid.Y + normal.Y * half));
        }

        // 4) 前端两条深色口钩
        double hookBase = radii[0] * 0.85;
        Point mouth = new(
            centers[0].X + dir.X * hookBase,
            centers[0].Y + dir.Y * hookBase);
        double spread = radii[0] * 0.42;
        double len = radii[0] * 0.95;
        dc.DrawLine(MouthHookPen,
            new Point(mouth.X - normal.X * spread, mouth.Y - normal.Y * spread),
            new Point(mouth.X - normal.X * spread * 0.45 + dir.X * len, mouth.Y - normal.Y * spread * 0.45 + dir.Y * len));
        dc.DrawLine(MouthHookPen,
            new Point(mouth.X + normal.X * spread, mouth.Y + normal.Y * spread),
            new Point(mouth.X + normal.X * spread * 0.45 + dir.X * len, mouth.Y + normal.Y * spread * 0.45 + dir.Y * len));

        // 5) 顶部高光
        dc.DrawEllipse(Palette.Highlight30, null,
            new Point(centers[2].X - normal.X * radii[2] * 0.30, centers[2].Y - normal.Y * radii[2] * 0.30),
            radii[2] * 0.40, radii[2] * 0.26);
    }

    // ------------------------------------------------------------------
    // 蛹 PUPA —— 圆角胶囊 1 : 1.8，纵向渐变，3 条节纹，偶尔抽动
    // ------------------------------------------------------------------
    private static void DrawPupa(DrawingContext dc, CreatureVisual c, double time, bool reduceMotion)
    {
        double s = c.Size;
        double w = 0.42 * s;
        double h = 0.42 * s * 1.8;

        double twitch = 0.0;
        if (!reduceMotion && time >= c.TwitchAt && time < c.TwitchAt + 0.12)
        {
            // ±2°，持续 120ms
            double t = (time - c.TwitchAt) / 0.12;
            twitch = Math.Sin(t * Math.PI) * (Math.PI / 90.0) * Math.Sign(Math.Sin(c.Seed + time * 3.0));
        }

        dc.PushTransform(new RotateTransform(twitch * 180.0 / Math.PI, c.Position.X, c.Position.Y));

        // 圆角胶囊：圆角 = 宽度一半
        var rect = new Rect(c.Position.X - w * 0.5, c.Position.Y - h * 0.5, w, h);
        var capsule = new RectangleGeometry(rect, w * 0.5, w * 0.5);

        dc.DrawGeometry(Palette.PupaGradient, OutlinePen, capsule);

        // 裁剪在胶囊内画节纹与高光
        dc.PushClip(capsule);

        // 3 条横向节纹
        for (int i = 1; i <= 3; i++)
        {
            double y = rect.Top + rect.Height * (i / 4.0);
            dc.DrawLine(PupaBandPen,
                new Point(rect.Left + w * 0.06, y),
                new Point(rect.Right - w * 0.06, y));
        }

        // 顶部细长高光条（白 30%）
        dc.DrawRoundedRectangle(Palette.Highlight30, null,
            new Rect(c.Position.X - w * 0.16, rect.Top + h * 0.10, w * 0.20, h * 0.55),
            w * 0.10, w * 0.10);

        dc.Pop(); // clip
        dc.Pop(); // transform
    }

    // ------------------------------------------------------------------
    // 苍蝇 FLY（卡通）—— 腹部 + 胸部 + 头 + 复眼 + 双翅 + 6 腿
    // ------------------------------------------------------------------
    private static void DrawFly(DrawingContext dc, CreatureVisual c, double time, bool reduceMotion)
    {
        double s = c.Size;
        double heading = c.Heading;
        double deg = heading * 180.0 / Math.PI;

        dc.PushTransform(new RotateTransform(deg, c.Position.X, c.Position.Y));

        double abdomenRx = 0.34 * s;
        double abdomenRy = 0.34 * s * 1.4;
        // 腹部在后方（-X 方向）
        Point abdomen = new(c.Position.X - 0.40 * s, c.Position.Y);
        Point thorax = new(c.Position.X + 0.10 * s, c.Position.Y);
        Point head = new(c.Position.X + 0.42 * s, c.Position.Y);

        // 6 条腿：3px 线段，深色。后对更靠后、更张开。
        for (int i = 0; i < 3; i++)
        {
            double baseX = c.Position.X + (0.24 - i * 0.20) * s;
            double len = (0.46 + i * 0.06) * s;
            for (int side = -1; side <= 1; side += 2)
            {
                double ang = side * (0.62 + i * 0.06) * Math.PI;
                Point knee = new(baseX + Math.Cos(ang) * len * 0.55, c.Position.Y + Math.Sin(ang) * len * 0.55);
                Point tip = new(baseX + Math.Cos(ang) * len * 1.05, c.Position.Y + Math.Sin(ang) * len * 0.92);
                dc.DrawLine(LegPen, new Point(baseX, c.Position.Y), knee);
                dc.DrawLine(LegPen, knee, tip);
            }
        }

        // 腹部（先画，略在胸部之下）
        dc.DrawEllipse(Palette.FlyBrush, FlyRimPen, abdomen, abdomenRx, abdomenRy);

        // 胸部
        dc.DrawEllipse(Palette.FlyBrush, FlyRimPen, thorax, 0.26 * s, 0.26 * s);

        // 头 + 两只复眼
        dc.DrawEllipse(Palette.FlyBrush, FlyRimPen, head, 0.17 * s, 0.17 * s);
        dc.DrawEllipse(Palette.EyeBrush, null,
            new Point(head.X + 0.04 * s, head.Y - 0.09 * s), 0.075 * s, 0.075 * s);
        dc.DrawEllipse(Palette.EyeBrush, null,
            new Point(head.X + 0.04 * s, head.Y + 0.09 * s), 0.075 * s, 0.075 * s);

        // 背部高光
        dc.DrawEllipse(Palette.Highlight30, null,
            new Point(thorax.X - 0.05 * s, thorax.Y - 0.10 * s), 0.10 * s, 0.06 * s);

        dc.Pop(); // transform

        // ── 翅：从胸部两侧"铰接"向外后张开
        //
        // 旧版把翅当成两颗贴着背脊的长椭圆（长轴与体轴几乎平行、只转 ±24°），
        // 于是两只翅尖在尾部互相靠近，看起来像"向内收拢的叶片"——很别扭。
        // 现在改成绕**翅根**旋转：根在胸部两侧，翅身沿局部 +X 向外后伸展，
        // 拍动时张开角在 ~114° ↔ ~94° 之间摆动（顶视下就是"扇"）。
        double flap;
        if (reduceMotion)
        {
            flap = 0.55; // 静止姿态
        }
        else
        {
            flap = Math.Abs(Math.Sin(time * 42.0 + c.Seed));
            if (flap < 0.18) flap = 0.18;
        }

        const double wingLen = 0.56;   // 相对 s
        double wingWid = 0.25 * (1.10 - 0.50 * flap); // 抬起时顶视投影变窄

        for (int side = -1; side <= 1; side += 2)
        {
            // 翅根在**胸部**两侧（偏前）：根放太后，翅身会横压在胖腹部上，像两片花瓣。
            Point root = new(c.Position.X + 0.01 * s, c.Position.Y + side * 0.13 * s);

            dc.PushTransform(new RotateTransform(deg, c.Position.X, c.Position.Y));
            // 128° ≈ 向后外侧张开成 V 字（纯侧向 90°、纯向后 180°）；
            // 拍动时向前扫 16°，读起来就是"扇翅"。
            dc.PushTransform(new RotateTransform(side * (128.0 - 16.0 * flap), root.X, root.Y));
            dc.PushTransform(new TranslateTransform(root.X, root.Y));
            dc.DrawEllipse(
                Palette.WingGradient,
                FlyRimPen,
                new Point(wingLen * s * 0.5, 0),
                wingLen * s * 0.5,
                wingWid * s * 0.5);
            dc.Pop(); // translate
            dc.Pop(); // 张开角
            dc.Pop(); // 体轴朝向
        }
    }
}
