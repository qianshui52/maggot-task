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
    private readonly SplatField _splats = new();

    /// <summary>当前虚拟桌面边界（逻辑像素）。</summary>
    public Rect Bounds { get; set; } = new(0, 0, 1920, 1080);

    public SplatField Splats => _splats;

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

    /// <summary>拍死时立刻移除生物（原地爆浆由 <see cref="SplatField"/> 负责）。</summary>
    public bool RemoveAt(string taskId, Stage stage, double now)
    {
        if (!_creatures.TryGetValue(taskId, out CreatureVisual? c)) return false;
        _splats.Spawn(c.Position, stage, now);
        _creatures.Remove(taskId);
        return true;
    }

    /// <summary>按 id 取生物（点杀判定用）。</summary>
    public bool TryGet(string taskId, out CreatureVisual creature) =>
        _creatures.TryGetValue(taskId, out creature!);

    public void Clear()
    {
        _creatures.Clear();
        _splats.Clear();
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
        _splats.Update(now, dt);

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

    /// <summary>命中判定：找出点(px,py)下方的苍蝇（用于「拍死」）。</summary>
    public CreatureVisual? HitTest(Point p, double radiusScale = 1.35)
    {
        CreatureVisual? best = null;
        double bestDist = double.MaxValue;

        foreach (CreatureVisual c in _creatures.Values)
        {
            double r = c.Size * 0.85 * radiusScale;
            double dx = c.Position.X - p.X;
            double dy = c.Position.Y - p.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 <= r * r && d2 < bestDist)
            {
                best = c;
                bestDist = d2;
            }
        }

        return best;
    }
}
