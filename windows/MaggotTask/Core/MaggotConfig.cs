using System.Text.Json.Serialization;

namespace MaggotTask.Core;

/// <summary>
/// 全局配置。默认值与 docs/lifecycle-spec.md 第 3 节完全一致。
/// </summary>
public sealed class MaggotConfig
{
    /// <summary>启动 / 暂停总开关。</summary>
    public bool Running { get; set; } = true;

    /// <summary>腐化倍速，0.25 .. 8.0。</summary>
    public double RotSpeed { get; set; } = 1.0;

    /// <summary>rotSpeed = 1 时 0 → 1 所需的分钟数。</summary>
    public int BaseRotMinutes { get; set; } = 90;

    public bool BreedEnabled { get; set; } = true;

    /// <summary>交配间隔基准（秒，受 rotSpeed 影响）。</summary>
    public int BreedSeconds { get; set; } = 40;

    /// <summary>苍蝇数量上限（安全阀，硬性）。</summary>
    public int MaxFlies { get; set; } = 60;

    /// <summary>任务总数上限（安全阀，硬性）。</summary>
    public int MaxTasks { get; set; } = 150;

    /// <summary>虫群层是否穿透鼠标 / 触摸。</summary>
    public bool ClickThrough { get; set; } = true;

    /// <summary>程序化音效（规格 §13）。默认开启 —— 用户装了它就是要听见它。</summary>
    public bool SoundEnabled { get; set; } = true;

    public bool NotifyOnFly { get; set; } = true;

    /// <summary>每日任务总开关（规格 §3 / §12.4）。false 时完全不生成，已有实例不受影响。</summary>
    public bool DailyEnabled { get; set; } = true;

    /// <summary>每周任务总开关（规格 §3 / §15.2）。false 时完全不生成，已有实例不受影响。</summary>
    public bool WeeklyEnabled { get; set; } = true;

    /// <summary>
    /// 减少动效。null = 跟随系统（SystemParameters.ClientAreaAnimation）。
    /// 规格未覆盖此项的持久化，此处按「跟随系统 + 可手动覆盖」处理。
    /// </summary>
    public bool? ReduceMotionOverride { get; set; }

    /// <summary>把越界数值夹回合法区间（json 被手改过也不至于崩）。</summary>
    public void Normalize()
    {
        RotSpeed = double.IsFinite(RotSpeed) ? Math.Clamp(RotSpeed, 0.25, 8.0) : 1.0;
        BaseRotMinutes = Math.Clamp(BaseRotMinutes, 1, 24 * 60);
        BreedSeconds = Math.Clamp(BreedSeconds, 2, 3600);
        MaxFlies = Math.Clamp(MaxFlies, 1, 2000);
        MaxTasks = Math.Clamp(MaxTasks, 1, 5000);
    }

    public MaggotConfig Clone() => new()
    {
        Running = Running,
        RotSpeed = RotSpeed,
        BaseRotMinutes = BaseRotMinutes,
        BreedEnabled = BreedEnabled,
        BreedSeconds = BreedSeconds,
        MaxFlies = MaxFlies,
        MaxTasks = MaxTasks,
        ClickThrough = ClickThrough,
        SoundEnabled = SoundEnabled,
        NotifyOnFly = NotifyOnFly,
        DailyEnabled = DailyEnabled,
        WeeklyEnabled = WeeklyEnabled,
        ReduceMotionOverride = ReduceMotionOverride,
    };
}
