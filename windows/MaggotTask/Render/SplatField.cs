using System.Windows;
using System.Windows.Media;
using MaggotTask.Core;

namespace MaggotTask.Render;

/// <summary>
/// 爆浆粒子。设计系统 §4 动效：<c>--dur-hero</c> 620ms；
/// reduced-motion 下退化为一次淡出（见 <see cref="Draw"/>）。
/// </summary>
public sealed class SplatField
{
    /// <summary>一支粒子（位置 + 速度 + 寿命）。</summary>
    private struct Splat
    {
        public Point P;
        public Vector V;
        public double BornAt;
        public double Life;
        public double Radius;
        public int ColorIndex;
    }

    /// <summary>爆浆持续时间（秒）= 620ms。</summary>
    public const double Duration = 0.620;

    private static readonly Brush[] Fluids =
    {
        Palette.SplatBrush(Stage.Egg),
        Palette.SplatBrush(Stage.Larva),
        Palette.SplatBrush(Stage.Pupa),
        Palette.SplatBrush(Stage.Fly),
    };

    private static readonly Pen SplatOutline = new(Palette.OutlineBrush, 1.0)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    };

    private readonly List<Splat> _splats = new();
    private readonly Random _rng = new();

    public bool IsEmpty => _splats.Count == 0;

    /// <summary>在某处炸开一坨。</summary>
    public void Spawn(Point at, Stage stage, double now)
    {
        int colorIndex = stage switch
        {
            Stage.Egg => 0,
            Stage.Larva => 1,
            Stage.Pupa => 2,
            _ => 3,
        };

        int count = 16 + _rng.Next(8);
        for (int i = 0; i < count; i++)
        {
            double ang = _rng.NextDouble() * Math.PI * 2.0;
            double speed = 30.0 + _rng.NextDouble() * 130.0;
            _splats.Add(new Splat
            {
                P = at,
                V = new Vector(Math.Cos(ang) * speed, Math.Sin(ang) * speed - 40.0),
                BornAt = now,
                Life = Duration * (0.55 + _rng.NextDouble() * 0.45),
                Radius = 1.4 + _rng.NextDouble() * 3.4,
                ColorIndex = colorIndex,
            });
        }
    }

    /// <summary>清空（重置/清空任务时用）。</summary>
    public void Clear() => _splats.Clear();

    /// <summary>推进并淘汰过期粒子。</summary>
    public void Update(double now, double dt)
    {
        if (_splats.Count == 0) return;

        for (int i = _splats.Count - 1; i >= 0; i--)
        {
            Splat s = _splats[i];
            double age = now - s.BornAt;
            if (age >= s.Life || age > Duration)
            {
                _splats.RemoveAt(i);
                continue;
            }

            s.V = new Vector(s.V.X * 0.965, s.V.Y * 0.965 + 260.0 * dt);
            s.P = new Point(s.P.X + s.V.X * dt, s.P.Y + s.V.Y * dt);
            _splats[i] = s;
        }
    }

    /// <summary>
    /// 绘制。reduced-motion 下不画粒子，只留一次淡出（一个原地扩散的圆）。
    /// </summary>
    public void Draw(DrawingContext dc, double now, bool reduceMotion)
    {
        if (_splats.Count == 0) return;

        if (reduceMotion)
        {
            // 退化：取最年轻的一支，画一次淡出。
            double youngest = double.MaxValue;
            Point at = default;
            int ci = 0;
            foreach (Splat s in _splats)
            {
                if (s.BornAt < youngest)
                {
                    youngest = s.BornAt;
                    at = s.P;
                    ci = s.ColorIndex;
                }
            }
            double t = Math.Clamp((now - youngest) / Duration, 0.0, 1.0);
            double r = 6.0 + 22.0 * t;
            dc.PushOpacity(0.55 * (1.0 - t));
            dc.DrawEllipse(Fluids[ci], null, at, r, r);
            dc.Pop();
            return;
        }

        foreach (Splat s in _splats)
        {
            double t = Math.Clamp((now - s.BornAt) / s.Life, 0.0, 1.0);
            double alpha = 1.0 - t * t;
            double r = s.Radius * (1.0 - 0.35 * t);
            // 尾迹：速度方向拉长，看起来更"湿"。
            var geo = new EllipseGeometry(s.P, r * (1.0 + 0.5 * (1.0 - t)), r);
            dc.PushOpacity(alpha);
            dc.DrawGeometry(Fluids[s.ColorIndex], null, geo);
            dc.Pop();
        }
    }
}
