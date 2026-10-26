using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MaggotTask.Core;

namespace MaggotTask.Render;

/// <summary>
/// 击杀的「洁净消除」特效 —— 明确**不血腥、不恶心**：
///
///   · 没有体液、没有飞溅的肉块、没有下垂的拖尾
///   · 只有：一圈扩散光环 + 几点向上飘的光尘 + 一个升起的「+N」奖励数字
///   · 颜色取该阶段的语义色再提亮，不做暗红 / 褐黑
///
/// 这样既保留了"打中了"的确定感，又不会让人在吃饭时被恶心到。
/// reduced-motion 下退化为一次淡出光环（不画飞散粒子）。
/// </summary>
public sealed class KillBurstField
{
    /// <summary>特效总时长（秒）。</summary>
    public const double Duration = 0.72;

    private struct Spark
    {
        public Point P;
        public Vector V;
        public double BornAt;
        public double Life;
        public double Radius;
        public Color Color;
        public bool Diamond;
    }

    private struct Ring
    {
        public Point P;
        public double BornAt;
        public double Life;
        public double From;
        public double To;
        public Color Color;
        public double Alpha;
    }

    private struct FloatText
    {
        public Point P;
        public double BornAt;
        public double Life;
        public string Text;
        public Color Color;
    }

    private static readonly Typeface RewardTypeface = new(
        new FontFamily("Microsoft YaHei UI"),
        FontStyles.Normal,
        FontWeights.Bold,
        FontStretches.Normal);

    /// <summary>金色 —— 货币的颜色，四阶段通用。</summary>
    private static readonly Color Gold = Color.FromRgb(0xF5, 0xC5, 0x42);

    private readonly List<Spark> _sparks = new();
    private readonly List<Ring> _rings = new();
    private readonly List<FloatText> _texts = new();
    private readonly Random _rng = new();

    public bool IsEmpty => _sparks.Count == 0 && _rings.Count == 0 && _texts.Count == 0;

    /// <summary>存活的特效元素数（诊断用）。</summary>
    public int ElementCount => _sparks.Count + _rings.Count + _texts.Count;

    /// <summary>
    /// 在世界坐标 <paramref name="at"/> 处炸开一次消除特效。
    /// </summary>
    /// <param name="at">命中点（覆盖层本地坐标）。</param>
    /// <param name="stage">被击杀的阶段（决定配色）。</param>
    /// <param name="reward">获得的货币；&gt;0 时飘一个「+N」。</param>
    /// <param name="now">当前秒。</param>
    /// <param name="ringRadius">光环最终半径（范围武器给大一圈，单点给小圈）。</param>
    public void Spawn(Point at, Stage stage, int reward, double now, double ringRadius = 34.0)
    {
        Color baseColor = Bright(Palette.StageColor(stage), 0.22);

        _rings.Add(new Ring
        {
            P = at,
            BornAt = now,
            Life = Duration * 0.62,
            From = 4.0,
            To = Math.Max(18.0, ringRadius),
            Color = baseColor,
            Alpha = 0.85,
        });

        // 第二圈错开 40ms、更大更淡 —— 像涟漪，不吓人。
        _rings.Add(new Ring
        {
            P = at,
            BornAt = now + 0.04,
            Life = Duration * 0.72,
            From = 2.0,
            To = Math.Max(26.0, ringRadius * 1.55),
            Color = baseColor,
            Alpha = 0.42,
        });

        int count = 10 + _rng.Next(6);
        for (int i = 0; i < count; i++)
        {
            double ang = _rng.NextDouble() * Math.PI * 2.0;
            double speed = 26.0 + _rng.NextDouble() * 92.0;
            _sparks.Add(new Spark
            {
                P = at,
                // 统一带一点向上的初速：光尘"飘起来"而不是"溅出去"
                V = new Vector(Math.Cos(ang) * speed, Math.Sin(ang) * speed - 34.0),
                BornAt = now + _rng.NextDouble() * 0.05,
                Life = 0.42 + _rng.NextDouble() * 0.26,
                Radius = 1.2 + _rng.NextDouble() * 2.2,
                Color = i % 3 == 0 ? Gold : baseColor,
                Diamond = i % 2 == 0,
            });
        }

        if (reward > 0)
        {
            _texts.Add(new FloatText
            {
                P = new Point(at.X, at.Y - 8.0),
                BornAt = now,
                Life = Duration,
                Text = "+" + reward.ToString(CultureInfo.InvariantCulture),
                Color = Gold,
            });
        }
    }

    /// <summary>清空（重置 / 清空全部任务时用）。</summary>
    public void Clear()
    {
        _sparks.Clear();
        _rings.Clear();
        _texts.Clear();
    }

    /// <summary>推进并淘汰过期元素。</summary>
    public void Update(double now, double dt)
    {
        for (int i = _sparks.Count - 1; i >= 0; i--)
        {
            Spark s = _sparks[i];
            double age = now - s.BornAt;
            if (age >= s.Life || age > Duration) { _sparks.RemoveAt(i); continue; }

            // 轻阻尼 + 极小的向上浮力（负重力），永远不下坠
            s.V = new Vector(s.V.X * 0.955, s.V.Y * 0.955 - 26.0 * dt);
            s.P = new Point(s.P.X + s.V.X * dt, s.P.Y + s.V.Y * dt);
            _sparks[i] = s;
        }

        for (int i = _rings.Count - 1; i >= 0; i--)
        {
            Ring r = _rings[i];
            if (now - r.BornAt >= r.Life || now - r.BornAt > Duration) _rings.RemoveAt(i);
        }

        for (int i = _texts.Count - 1; i >= 0; i--)
        {
            FloatText t = _texts[i];
            if (now - t.BornAt >= t.Life || now - t.BornAt > Duration) _texts.RemoveAt(i);
        }
    }

    /// <summary>绘制。</summary>
    /// <param name="pixelsPerDip">当前 DPI 缩放（FormattedText 需要，否则 125% 下文字发虚）。</param>
    public void Draw(DrawingContext dc, double now, bool reduceMotion, double pixelsPerDip)
    {
        if (IsEmpty) return;

        foreach (Ring r in _rings)
        {
            double t = Math.Clamp((now - r.BornAt) / r.Life, 0.0, 1.0);
            if (t <= 0) continue;

            // ease-out：一开始扩得快，后面收住
            double e = 1.0 - (1.0 - t) * (1.0 - t);
            double radius = r.From + (r.To - r.From) * e;
            double alpha = r.Alpha * (1.0 - t);

            if (reduceMotion)
            {
                dc.PushOpacity(alpha * 0.8);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(r.Color), 2.0), r.P, radius, radius);
                dc.Pop();
                continue;
            }

            var brush = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(alpha * 255.0, 0, 255), r.Color.R, r.Color.G, r.Color.B));
            brush.Freeze();
            var pen = new Pen(brush, 2.4 * (1.0 - 0.45 * t));
            pen.Freeze();
            dc.DrawEllipse(null, pen, r.P, radius, radius);
        }

        if (!reduceMotion)
        {
            foreach (Spark s in _sparks)
            {
                double age = now - s.BornAt;
                if (age < 0) continue;

                double t = Math.Clamp(age / s.Life, 0.0, 1.0);
                double alpha = 1.0 - t * t;
                byte a = (byte)Math.Clamp(alpha * 235.0, 0, 255);
                if (a == 0) continue;

                var brush = new SolidColorBrush(Color.FromArgb(a, s.Color.R, s.Color.G, s.Color.B));
                brush.Freeze();

                double r = s.Radius * (1.0 - 0.30 * t);
                if (s.Diamond)
                {
                    var geo = new StreamGeometry();
                    using (StreamGeometryContext ctx = geo.Open())
                    {
                        ctx.BeginFigure(new Point(s.P.X, s.P.Y - r * 1.7), true, true);
                        ctx.LineTo(new Point(s.P.X + r, s.P.Y), true, false);
                        ctx.LineTo(new Point(s.P.X, s.P.Y + r * 1.7), true, false);
                        ctx.LineTo(new Point(s.P.X - r, s.P.Y), true, false);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(brush, null, geo);
                }
                else
                {
                    dc.DrawEllipse(brush, null, s.P, r, r);
                }
            }
        }

        foreach (FloatText t in _texts)
        {
            double age = now - t.BornAt;
            if (age < 0) continue;

            double k = Math.Clamp(age / t.Life, 0.0, 1.0);
            // 前 35% 快速上升，之后减速停住并淡出
            double rise = reduceMotion ? 0.0 : 34.0 * (1.0 - (1.0 - k) * (1.0 - k));
            double alpha = k < 0.15 ? k / 0.15 : 1.0 - (k - 0.15) / 0.85;
            byte a = (byte)Math.Clamp(alpha * 255.0, 0, 255);
            if (a == 0) continue;

            var brush = new SolidColorBrush(Color.FromArgb(a, t.Color.R, t.Color.G, t.Color.B));
            brush.Freeze();

            var ft = new FormattedText(
                t.Text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                RewardTypeface,
                17.0,
                brush,
                pixelsPerDip);

            // 落在命中点正上方、水平居中
            dc.DrawText(ft, new Point(t.P.X - ft.Width / 2.0, t.P.Y - rise - ft.Height));
        }
    }

    /// <summary>把颜色往白色方向提亮，避免出现"脏"的暗色粒子。</summary>
    private static Color Bright(Color c, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromRgb(
            (byte)(c.R + (255 - c.R) * amount),
            (byte)(c.G + (255 - c.G) * amount),
            (byte)(c.B + (255 - c.B) * amount));
    }
}
