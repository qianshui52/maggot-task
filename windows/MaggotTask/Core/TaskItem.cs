using System.Text.Json.Serialization;

namespace MaggotTask.Core;

/// <summary>
/// 一个任务就是一只虫子。
/// 字段顺序与 docs/lifecycle-spec.md 第 3 节一一对应。
/// </summary>
public sealed class TaskItem
{
    /// <summary>UUID。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    /// <summary>任务标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>备注，可为空。</summary>
    public string Note { get; set; } = string.Empty;

    public TaskStatus Status { get; set; } = TaskStatus.Todo;

    /// <summary>腐烂度，0.0 .. <see cref="LifecycleEngine.RotMax"/>。</summary>
    public float Rot { get; set; }

    /// <summary>进度，0 .. 100。</summary>
    public int Progress { get; set; }

    /// <summary>epoch millis。</summary>
    public long CreatedAt { get; set; }

    /// <summary>epoch millis，创建时等于 <see cref="CreatedAt"/>。</summary>
    public long LastCaredAt { get; set; }

    public long? CompletedAt { get; set; }

    /// <summary>0 = 用户亲手创建；&gt;0 = 苍蝇繁殖出来的。</summary>
    public int Generation { get; set; }

    /// <summary>繁殖来源。</summary>
    public string? ParentId { get; set; }

    /// <summary>= min(1 + GENE_STEP*generation, GENE_MAX)。</summary>
    public float Gene { get; set; } = 1.0f;

    /// <summary>
    /// 非空 = 这是某个每日任务模板生成的实例（规格 §3 / §12）。
    /// 模板删除后这里会变成孤儿 id —— 实例退化成普通任务继续烂（§12.4）。
    /// </summary>
    public string? DailyTemplateId { get; set; }

    /// <summary>
    /// "YYYY-MM-DD"（设备本地时区）；仅每日实例有值（规格 §3 / §12）。
    /// </summary>
    public string? DailyKey { get; set; }

    /// <summary>
    /// 非空 = 这是某个每周任务模板生成的实例（规格 §3 / §15.2）。
    /// 模板删除后这里会变成孤儿 id —— 实例退化成普通任务继续烂（§15.5 → §12.4）。
    /// </summary>
    public string? WeeklyTemplateId { get; set; }

    /// <summary>
    /// ISO 周键 "YYYY-Www"（周基准年，设备本地时区）；仅每周实例有值（规格 §3 / §15.2）。
    /// </summary>
    public string? WeeklyKey { get; set; }

    public TaskItem Clone() => new()
    {
        Id = Id,
        Title = Title,
        Note = Note,
        Status = Status,
        Rot = Rot,
        Progress = Progress,
        CreatedAt = CreatedAt,
        LastCaredAt = LastCaredAt,
        CompletedAt = CompletedAt,
        Generation = Generation,
        ParentId = ParentId,
        Gene = Gene,
        DailyTemplateId = DailyTemplateId,
        DailyKey = DailyKey,
        WeeklyTemplateId = WeeklyTemplateId,
        WeeklyKey = WeeklyKey,
    };

    /// <summary>归一化腐烂度，用于进度条/剩余时间显示。</summary>
    [JsonIgnore]
    public double RotRatio => Math.Clamp(Rot / LifecycleEngine.FlyT, 0.0, 1.0);

    public override string ToString() =>
        $"TaskItem({Id[..Math.Min(8, Id.Length)]} {Title} rot={Rot:0.###} gen={Generation} gene={Gene:0.##})";
}
