using System.Windows;
using System.Windows.Media;
using MaggotTask.Core;
using MaggotTask.Render;

namespace MaggotTask.View;

/// <summary>
/// 虫群模型：把 <see cref="TaskItem"/> 映射为屏幕上的生物，跑位置/动线，
/// 并处理「新生物从屏幕边缘外飞入」与入场淡入。
///
/// 设计系统 §6 的轻量 boids 就实现在这里。
/// </summary>
public sealed class SwarmModel
{
    /// <summary>边缘规避触发距离（px）。</summary>
    public const double EdgeMargin = 60.0;

    private readonly Dictionary<string, CreatureVisual> _creatures = new(StringComparer.Ordinal);
    private readonly Random _rng = new();

    /// <summary>当前虚拟桌面边界（逻辑像素）。</summary>
    public Rect Bounds { get; set; } = new(0, 0, 1920, 1080);

    public int Count => _creatures.Count;

    public IReadOnlyCollection<CreatureVisual> Creatures => _creatures.Values;

    /// <summary>按任务列表同步生物集合：新增的飞入，删掉的移除，已拍死的转入爆浆。</summary>
    public void Sync(IReadOnlyList<TaskItem> tasks, double now)
    {
        HashSet<string> alive = new(StringComparer.Ordinal);

        foreach (TaskItem t in tasks)
        {
            if (t.Status == TaskStatus.Done) continue;

            alive.Add(t.Id);
            if (!_creatures.TryGetValue(t.Id, out CreatureVisual? c))
            {
                c = new CreatureVisual(t.Id, Rng.SeedFrom(t.Id))
                {
                    SpawnedAt = now,
                    SpawnProgress = 0.0,
                    NextTwitchAt = now + _rng.NextDouble() * 4.0,
                    NextDashAt = now + _rng.NextDouble() * 2.5,
                };
                SpawnOutside(c, t.Id);
                _creatures[t.Id] = c;
            }

            c.Stage = LifecycleEngine.StageOf(t);
            c.Rot = t.Rot;
        }

        // 清理已经不在任务列表里的生物
        if (_creatures.Count != alive.Count)
        {
            List<string> stale = new();
            foreach (string id in _creatures.Keys)
            {
                if (!alive.Contains(id)) stale.Add(id);
            }
            foreach (string id in stale) _creatures.Remove(id);
        }
    }

    /// <summary>
    /// 击杀时立刻移除生物。
    /// 消除特效由 <see cref="MaggotTask.Render.KillBurstField"/> 在覆盖层里负责 ——
    /// 模型本身不持有任何特效状态，避免"模型层偷偷画东西"。
    /// </summary>
    public bool RemoveAt(string taskId)
    {
        return _creatures.Remove(taskId);
    }

    /// <summary>按 id 取生物（点杀判定用）。</summary>
    public bool TryGet(string taskId, out CreatureVisual creature) =>
        _creatures.TryGetValue(taskId, out creature!);

    public void Clear()
    {
        _creatures.Clear();
    }

    /// <summary>新生物从随机的屏幕边缘外飞入。</summary>
    private void SpawnOutside(CreatureVisual c, string id)
    {
        Rect b = Bounds;
        double margin = 60.0;
        var rng = new Rng(Rng.SeedFrom(id) ^ 0x5BF03635u);
        int side = (int)(rng.NextUInt() % 4);

        c.Position = side switch
        {
            0 => new Point(rng.Range(b.Left, b.Right), b.Top - margin),           // 上
            1 => new Point(b.Right + margin, rng.Range(b.Top, b.Bottom)),         // 右
            2 => new Point(rng.Range(b.Left, b.Right), b.Bottom + margin),        // 下
            _ => new Point(b.Left - margin, rng.Range(b.Top, b.Bottom)),          // 左
        };

        // 朝屏幕内
        var toCenter = new Vector(b.Left + b.Width * 0.5 - c.Position.X, b.Top + b.Height * 0.5 - c.Position.Y);
        if (toCenter.Length < 0.001) toCenter = new Vector(1, 0);
        toCenter.Normalize();
        c.Heading = Math.Atan2(toCenter.Y, toCenter.X);
        c.WanderAngle = c.Heading;
        c.Velocity = toCenter * 40.0;
    }

    /// <summary>
    /// 推进一帧。dt 单位为秒（调用方已 clamp）。
    /// </summary>
    public void Update(double now, double dt, bool reduceMotion)
    {
        Rect b = Bounds;
        foreach (CreatureVisual c in _creatures.Values)
        {
            // 入场淡入：0.45 秒
            if (c.SpawnProgress < 1.0)
            {
                c.SpawnProgress = Math.Clamp((now - c.SpawnedAt) / 0.45, 0.0, 1.0);
            }

            switch (c.Stage)
            {
                case Stage.Egg:
                case Stage.Pupa:
                    UpdateSessile(c, now, dt, reduceMotion);
                    break;
                case Stage.Larva:
                    UpdateLarva(c, now, dt, reduceMotion);
                    break;
                case Stage.Fly:
                    UpdateFly(c, now, dt, reduceMotion);
                    break;
            }

            ClampInside(c, b);
        }
    }

    /// <summary>卵 / 蛹：完全静止（原地轻微动画）。</summary>
    private void UpdateSessile(CreatureVisual c, double now, double dt, bool reduceMotion)
    {
        if (c.Stage != Stage.Pupa) return;

        if (reduceMotion)
        {
            c.TwitchAt = double.MinValue;
            return;
        }

        if (now >= c.NextTwitchAt)
        {
            // 每 3.5–6 秒抽动一次
            c.TwitchAt = now;
            c.NextTwitchAt = now + 3.5 + _rng.NextDouble() * 2.5;
        }
    }

    /// <summary>蛆：几乎不动，6 px/s 缓慢拱行，碰到边缘折返。</summary>
    private static void UpdateLarva(CreatureVisual c, double now, double dt, bool reduceMotion)
    {
        if (c.Pinned) return;   // 诊断用：钉住不动

        double speed = reduceMotion ? 0.0 : 6.0;

        // 朝向随机缓慢改变
        var rng = new Rng(c.Seed ^ (uint)(now * 3.0));
        c.WanderAngle += (rng.NextDouble() - 0.5) * 0.35 * dt;

        var dir = new Vector(Math.Cos(c.WanderAngle), Math.Sin(c.WanderAngle));
        c.Position = new Point(c.Position.X + dir.X * speed * dt, c.Position.Y + dir.Y * speed * dt);
        c.Heading = c.WanderAngle;
    }

    /// <summary>
    /// 苍蝇：轻量 boids（设计系统 §6）。
    /// speed = 70 + 60*min(rot,1)，越烂越狂躁；每 1–3 秒一次随机冲刺。
    /// </summary>
    private void UpdateFly(CreatureVisual c, double now, double dt, bool reduceMotion)
    {
        if (c.Pinned)
        {
            // 诊断用：钉住不飞（否则点下去时它已经飞走了）
            c.Velocity = new Vector(0, 0);
            return;
        }

        double speed = 70.0 + 60.0 * c.Intensity;
        if (reduceMotion)
        {
            // reduced-motion：不飞行，原地悬停（速度归零），朝向固定。
            c.Velocity = new Vector(0, 0);
            c.WanderAngle = c.Heading;
            return;
        }

        // wanderAngle += rand(-1,1) * 0.35 * dt * 60 * 0.016
        c.WanderAngle += (_rng.NextDouble() * 2.0 - 1.0) * 0.35 * dt * 60.0 * 0.016;

        var desired = new Vector(Math.Cos(c.WanderAngle), Math.Sin(c.WanderAngle)) * speed;
        c.Velocity = new Vector(
            c.Velocity.X + (desired.X - c.Velocity.X) * 0.06,
            c.Velocity.Y + (desired.Y - c.Velocity.Y) * 0.06);

        // 每 1–3 秒随机一次冲刺：vel += randUnit() * (60 + 90*min(rot,1))
        if (now >= c.NextDashAt)
        {
            c.NextDashAt = now + 1.0 + _rng.NextDouble() * 2.0;
            double ang = _rng.NextDouble() * Math.PI * 2.0;
            double mag = 60.0 + 90.0 * c.Intensity;
            c.Velocity += new Vector(Math.Cos(ang) * mag, Math.Sin(ang) * mag);
            // 硬上限，防止速度累积成瞬移
            double max = speed * 3.2;
            if (c.Velocity.Length > max)
            {
                c.Velocity = c.Velocity / c.Velocity.Length * max;
            }
        }

        // 边缘规避：距边界 < 60px 时向内的转向力随距离线性增强
        Rect b = Bounds;
        var steer = new Vector(0, 0);
        double left = c.Position.X - b.Left;
        double right = b.Right - c.Position.X;
        double top = c.Position.Y - b.Top;
        double bottom = b.Bottom - c.Position.Y;

        if (left < EdgeMargin) steer.X += (1.0 - left / EdgeMargin) * speed * 1.6;
        if (right < EdgeMargin) steer.X -= (1.0 - right / EdgeMargin) * speed * 1.6;
        if (top < EdgeMargin) steer.Y += (1.0 - top / EdgeMargin) * speed * 1.6;
        if (bottom < EdgeMargin) steer.Y -= (1.0 - bottom / EdgeMargin) * speed * 1.6;

        c.Velocity += steer * dt * 6.0;
        c.Position = new Point(c.Position.X + c.Velocity.X * dt, c.Position.Y + c.Velocity.Y * dt);

        if (c.Velocity.LengthSquared > 1.0)
        {
            c.Heading = Math.Atan2(c.Velocity.Y, c.Velocity.X);
        }
    }

    /// <summary>软性边界：出界就折返并拉回。</summary>
    private static void ClampInside(CreatureVisual c, Rect b)
    {
        double pad = 12.0;
        double x = c.Position.X;
        double y = c.Position.Y;
        bool bounced = false;

        if (x < b.Left + pad) { x = b.Left + pad; bounced = true; }
        else if (x > b.Right - pad) { x = b.Right - pad; bounced = true; }

        if (y < b.Top + pad) { y = b.Top + pad; bounced = true; }
        else if (y > b.Bottom - pad) { y = b.Bottom - pad; bounced = true; }

        if (bounced)
        {
            c.Position = new Point(x, y);
            // 折返：把朝向翻到屏幕内侧
            var inward = new Vector(
                Math.Sign(b.Left + b.Width * 0.5 - x) * 0.6,
                Math.Sign(b.Top + b.Height * 0.5 - y) * 0.6);
            if (inward.LengthSquared < 0.0001) inward = new Vector(1, 0);
            inward.Normalize();
            c.WanderAngle = Math.Atan2(inward.Y, inward.X);
            c.Heading = c.WanderAngle;
            c.Velocity = inward * Math.Max(20.0, c.Velocity.Length * 0.5);
        }
    }

    /// <summary>命中判定：找出点(px,py)下方的生物（用于「拍死」）。</summary>
    public CreatureVisual? HitTest(Point p, double radiusScale = 1.35)
    {
        CreatureVisual? best = null;
        double bestDist = double.MaxValue;

        foreach (CreatureVisual c in _creatures.Values)
        {
            // 判定中心用 HitCenter 而不是 Position：蛆的 Position 是头部。
            Point center = c.HitCenter;

            // 苍蝇以 70–130 px/s 乱飞、还会冲刺，等鼠标按下的那几十毫秒里就位移了。
            // 给它 20% 的判定加成：仍然是"最难打的阶段"，但不至于明明点上了却判空。
            double stageBoost = c.Stage == Stage.Fly ? 1.20 : 1.0;

            double r = c.Size * 0.85 * radiusScale * stageBoost;
            double dx = center.X - p.X;
            double dy = center.Y - p.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 <= r * r && d2 < bestDist)
            {
                best = c;
                bestDist = d2;
            }
        }

        return best;
    }

    /// <summary>
    /// 光标下有没有任何生物（用于"悬停自动接管点击"）。
    /// 与 <see cref="HitTest"/> 的区别：只回答有/没有，不挑最近的一只，也不排序。
    /// </summary>
    public bool HitTestAny(Point p, double radiusScale = 1.7)
    {
        foreach (CreatureVisual c in _creatures.Values)
        {
            Point center = c.HitCenter;
            // 与 HitTest 保持同一套加成，否则会出现"接管了却打不中"的窄缝。
            double stageBoost = c.Stage == Stage.Fly ? 1.20 : 1.0;
            double r = c.Size * 0.85 * radiusScale * stageBoost;
            double dx = center.X - p.X;
            double dy = center.Y - p.Y;
            if (dx * dx + dy * dy <= r * r) return true;
        }
        return false;
    }

    /// <summary>
    /// 按工具形状收集本次出手命中的全部生物。
    ///
    /// 形状规则（与 <see cref="ToolCatalog"/> 一一对应）：
    ///   Single —— 只取最近的一只，半径 size × 0.85 × 1.35（比悬停的 0.7 宽，手感更好）
    ///   Circle —— 圆心 <paramref name="p"/>、半径 tool.Radius
    ///   Cross  —— 横竖两条「长 2×Radius、半宽 Width」的带，取并集
    ///   Beam   —— 以 p.Y 为中心、半宽 Width 的全屏横向带
    /// </summary>
    public List<CreatureVisual> CollectHits(ToolDef tool, Point p)
    {
        var hits = new List<CreatureVisual>();

        switch (tool.Shape)
        {
            case ToolShape.Single:
            {
                CreatureVisual? best = HitTest(p);
                if (best is not null) hits.Add(best);
                break;
            }

            case ToolShape.Circle:
            {
                double r2 = tool.Radius * tool.Radius;
                foreach (CreatureVisual c in _creatures.Values)
                {
                    if (DistanceSquared(c.HitCenter, p) <= r2 + HalfExtentSquared(c)) hits.Add(c);
                }
                break;
            }

            case ToolShape.Cross:
            {
                double reach = tool.Radius;
                double half = tool.Width;
                foreach (CreatureVisual c in _creatures.Values)
                {
                    Point center = c.HitCenter;
                    double dx = Math.Abs(center.X - p.X);
                    double dy = Math.Abs(center.Y - p.Y);
                    double pad = c.Size * 0.45;   // 生物自身半径，避免"擦边不算"

                    bool horizontal = dx <= reach + pad && dy <= half + pad;
                    bool vertical = dy <= reach + pad && dx <= half + pad;
                    if (horizontal || vertical) hits.Add(c);
                }
                break;
            }

            case ToolShape.Beam:
            {
                double half = tool.Width;
                foreach (CreatureVisual c in _creatures.Values)
                {
                    if (Math.Abs(c.HitCenter.Y - p.Y) <= half + c.Size * 0.45) hits.Add(c);
                }
                break;
            }
        }

        return hits;
    }

    private static double DistanceSquared(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double HalfExtentSquared(CreatureVisual c) => c.Size * c.Size * 0.2;
}
