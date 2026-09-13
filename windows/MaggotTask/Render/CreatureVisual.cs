using MaggotTask.Core;

namespace MaggotTask.Render;

/// <summary>
/// 虫群层里的一只生物。位置按 task id 持久化在内存，阶段变化时不重置位置（避免瞬移）。
/// </summary>
public sealed class CreatureVisual
{
    /// <summary>
    /// 基准直径（设计系统 §5：size = 1.0 对应的直径）。
    ///
    /// 桌面端取 34 而不是规格里那个面向手机的 22：在 1536×864 的桌面上，
    /// 22px 只占屏宽 1.4%，实测渲染出来像一粒灰尘，完全没有"视觉压力"。
    /// 手机端 22dp 是合适的（约 6% 屏宽），桌面端需要更大的绝对值。
    /// </summary>
    public const double BaseSize = 34.0;

    public string TaskId { get; }
    public uint Seed { get; }

    public Point Position;
    public Vector Velocity;
    public double WanderAngle;

    /// <summary>朝向（弧度）。卵/蛹不用。</summary>
    public double Heading;

    /// <summary>开始入场的时刻（秒）。从屏幕边缘外飞入。</summary>
    public double SpawnedAt;

    /// <summary>入场的淡入/滑入进度 0 → 1。</summary>
    public double SpawnProgress = 1.0;

    /// <summary>本次抽动的时刻（蛹）。</summary>
    public double TwitchAt;
    public double NextTwitchAt;

    /// <summary>下一次冲刺的时刻（苍蝇）。</summary>
    public double NextDashAt;

    public Stage Stage = Stage.Egg;
    public float Rot;

    public CreatureVisual(string taskId, uint seed)
    {
        TaskId = taskId;
        Seed = seed;
    }

    /// <summary>尺寸系数：越烂越大一点点（最多 +15%）。规格未定义，属体验判断。</summary>
    public double SizeScale => 1.0 + 0.15 * Math.Min(Rot, LifecycleEngine.RotMaxSafe);

    /// <summary>动画强度：rot &gt; 1.0 之后只影响动画强度。</summary>
    public double Intensity => Math.Min(Rot, LifecycleEngine.RotMaxSafe);

    /// <summary>按帧绘制尺寸。</summary>
    public double Size => BaseSize * SizeScale;
}
