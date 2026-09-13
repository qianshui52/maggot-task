using System.Text.Json.Serialization;
using MaggotTask.Core;

namespace MaggotTask.Services;

/// <summary>落盘的根对象。</summary>
public sealed class AppState
{
    /// <summary>
    /// 格式版本。
    ///   v1 —— 没有每日任务字段（DailyTemplates 缺失、TaskItem 没有 daily* 字段）。
    ///   v2 —— 加入每日任务模板（规格 §12）。
    ///   v3 —— 加入每周任务模板（规格 §15）。
    /// 读取时**不做版本判断**：缺失字段一律取默认值，所以 v1 / v2 文件能直接读进来，不抛异常。
    /// </summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>当前写盘使用的格式版本。</summary>
    public const int CurrentVersion = 3;

    public List<TaskItem> Tasks { get; set; } = new();

    /// <summary>每日任务模板（规格 §3 / §12）。v1 文件里没有这个键 → 反序列化后为空列表。</summary>
    public List<DailyTemplate> DailyTemplates { get; set; } = new();

    /// <summary>每周任务模板（规格 §3 / §15）。v1 / v2 文件里没有这个键 → 反序列化后为空列表。</summary>
    public List<WeeklyTemplate> WeeklyTemplates { get; set; } = new();

    public MaggotConfig Config { get; set; } = new();

    /// <summary>上次退出/保存时的时间戳（epoch millis），用于离线补算。</summary>
    public long LastSeenAt { get; set; }

    /// <summary>累计拍死数量。</summary>
    public long SquashedCount { get; set; }

    // 控制面板窗口位置（顺手持久化，规格未要求，纯体验）。
    public double? PanelLeft { get; set; }
    public double? PanelTop { get; set; }
}

/// <summary>
/// System.Text.Json 源生成上下文 —— 完全 AOT/裁剪友好，且不需要任何 NuGet 包。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(AppState))]
[JsonSerializable(typeof(TaskItem))]
[JsonSerializable(typeof(DailyTemplate))]
[JsonSerializable(typeof(WeeklyTemplate))]
[JsonSerializable(typeof(MaggotConfig))]
[JsonSerializable(typeof(List<TaskItem>))]
[JsonSerializable(typeof(List<DailyTemplate>))]
[JsonSerializable(typeof(List<WeeklyTemplate>))]
internal sealed partial class StateJsonContext : JsonSerializerContext
{
}
