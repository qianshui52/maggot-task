namespace MaggotTask.Core;

/// <summary>引擎事件，供 UI / 通知消费（规格第 9 节）。</summary>
public enum EngineEventType
{
    /// <summary>卵自己孵了。</summary>
    Hatched,

    /// <summary>卡住变蛹。</summary>
    Pupated,

    /// <summary>rot 越过 0.68 预警线。</summary>
    ApproachingFly,

    /// <summary>rot 越过 0.78 → 推通知。</summary>
    BecameFly,

    /// <summary>交配产卵。</summary>
    Bred,

    /// <summary>拍死。</summary>
    Squashed,

    /// <summary>每日任务实例生成（规格 §12.2）。</summary>
    DailySpawned,

    /// <summary>每周任务实例生成（规格 §15.4）。</summary>
    WeeklySpawned,
}

/// <summary>单个引擎事件。</summary>
public readonly record struct EngineEvent(
    EngineEventType Type,
    string TaskId,
    string Title,
    string? SecondId = null,
    string? ChildId = null)
{
    /// <summary>交配事件的便捷构造。</summary>
    public static EngineEvent Bred(string aId, string bId, string childId) =>
        new(EngineEventType.Bred, aId, string.Empty, bId, childId);

    /// <summary>每日任务实例生成事件（<paramref name="templateId"/> 放在 SecondId 上）。</summary>
    public static EngineEvent DailySpawned(string taskId, string title, string templateId, string dailyKey) =>
        new(EngineEventType.DailySpawned, taskId, title, templateId, dailyKey);

    /// <summary>每周任务实例生成事件（<paramref name="templateId"/> 放在 SecondId 上）。</summary>
    public static EngineEvent WeeklySpawned(string taskId, string title, string templateId, string weekKey) =>
        new(EngineEventType.WeeklySpawned, taskId, title, templateId, weekKey);
}
