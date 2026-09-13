namespace MaggotTask.Core;

/// <summary>
/// 生命阶段。完全由 <see cref="TaskItem.Rot"/> 与 <see cref="TaskStatus"/> 推导，不由日历决定。
/// </summary>
public enum Stage
{
    /// <summary>卵 · rot &lt; 0.18 且未开始</summary>
    Egg = 0,

    /// <summary>蛆 · rot ≥ 0.18 或 status == DOING</summary>
    Larva = 1,

    /// <summary>蛹 · rot ≥ 0.50</summary>
    Pupa = 2,

    /// <summary>苍蝇 · rot ≥ 0.78</summary>
    Fly = 3,

    /// <summary>已拍死 · status == DONE</summary>
    Dead = 4,
}

/// <summary>任务状态。</summary>
public enum TaskStatus
{
    Todo = 0,
    Doing = 1,
    Done = 2,
}

public static class StageExtensions
{
    /// <summary>阶段中文名（用户可见文案，两端一致）。</summary>
    public static string Label(this Stage stage) => stage switch
    {
        Stage.Egg => "卵",
        Stage.Larva => "蛆",
        Stage.Pupa => "蛹",
        Stage.Fly => "苍蝇",
        Stage.Dead => "战果",
        _ => "未知",
    };

    /// <summary>阶段代号，用于 chip（11px 等宽大写）。</summary>
    public static string Code(this Stage stage) => stage switch
    {
        Stage.Egg => "EGG",
        Stage.Larva => "LARVA",
        Stage.Pupa => "PUPA",
        Stage.Fly => "FLY",
        Stage.Dead => "DEAD",
        _ => "?",
    };
}
