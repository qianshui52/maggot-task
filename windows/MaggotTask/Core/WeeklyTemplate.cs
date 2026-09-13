using System.Globalization;

namespace MaggotTask.Core;

/// <summary>
/// 每周任务模板（规格 §15.2）。
///
/// 与 <see cref="DailyTemplate"/> **完全同构**，只多一个 <see cref="Weekday"/> ——
/// 实例在这一天**或之后**生成：周一没开电脑、周三才打开，照样补上，不跳过这一周。
/// </summary>
public sealed class WeeklyTemplate
{
    /// <summary>UUID。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    /// <summary>模板标题（实例标题 = "&lt;title&gt; · W&lt;ww&gt;"）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>备注，可为空；生成实例时原样带过去。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>关掉后不再生成新实例（已有实例不受影响）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>epoch millis。</summary>
    public long CreatedAt { get; set; }

    /// <summary>列表排序。</summary>
    public int SortOrder { get; set; }

    /// <summary>ISO 星期几：1=周一 … 7=周日。实例在这一天**或之后**生成（规格 §15.2）。</summary>
    public int Weekday { get; set; } = 1;

    public WeeklyTemplate Clone() => new()
    {
        Id = Id,
        Title = Title,
        Note = Note,
        Enabled = Enabled,
        CreatedAt = CreatedAt,
        SortOrder = SortOrder,
        Weekday = Weekday,
    };

    public override string ToString() =>
        $"WeeklyTemplate({Id[..Math.Min(8, Id.Length)]} {Title} weekday={Weekday} enabled={Enabled} order={SortOrder})";
}

/// <summary>
/// 每周任务的时间与文案工具（规格 §15.3）。
///
/// 两个最容易写错的地方，这里都刻意写死：
///
/// 1. **周键必须用「周基准年」**（<see cref="ISOWeek.GetYear"/>，不是 <c>DateTime.Year</c>）。
///    普通年份在跨年那一周会算错 —— <c>2027-01-01</c> 属于 <c>2026-W53</c>，
///    用日历年份会得到 <c>2027-W53</c>，凭空多出一周。
/// 2. **星期一律用 ISO 编号 1=周一 … 7=周日**。
///    .NET 的 <see cref="DayOfWeek"/> 是 0=周日..6=周六，**必须转换** ——
///    写错会让每个模板整体错一天。
///
/// ⚠️ 时区：一律用**设备本地日期**，绝不用 UTC。
/// </summary>
public static class WeeklyKeys
{
    /// <summary>把本地时间格式化成 ISO 周键 "YYYY-Www"（周基准年 + 补零两位周号）。</summary>
    public static string FromLocal(DateTime local) =>
        $"{ISOWeek.GetYear(local)}-W{ISOWeek.GetWeekOfYear(local):D2}";

    /// <summary>把 epoch millis 按其本地时区格式化成 ISO 周键。</summary>
    public static string FromUnixMs(long unixMs) =>
        FromLocal(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime);

    /// <summary>本周的周键（设备本地日期）。</summary>
    public static string Today() => FromLocal(DateTime.Now);

    /// <summary>
    /// ISO 星期几：1=周一 … 7=周日（规格 §15.3）。
    /// .NET 的 <see cref="DayOfWeek"/> 是 0=周日..6=周六，这里做唯一的一处转换。
    /// </summary>
    public static int IsoWeekday(DateTime local) =>
        local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;

    /// <summary>把 epoch millis 按其本地时区换算成 ISO 星期几。</summary>
    public static int IsoWeekdayFromUnixMs(long unixMs) =>
        IsoWeekday(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime);

    /// <summary>今天的 ISO 星期几。</summary>
    public static int TodayWeekday() => IsoWeekday(DateTime.Now);

    /// <summary>供界面显示的星期名（规格 §15 的「星期几」列）。</summary>
    public static string WeekdayLabel(int iso) => iso switch
    {
        1 => "周一",
        2 => "周二",
        3 => "周三",
        4 => "周四",
        5 => "周五",
        6 => "周六",
        7 => "周日",
        _ => "周?",
    };

    /// <summary>"2026-W37" → "W37"，用于实例标题后缀；无法识别时原样返回。</summary>
    public static string ShortSuffix(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        int sep = key.IndexOf("-W", StringComparison.OrdinalIgnoreCase);
        return sep >= 0 ? "W" + key[(sep + 2)..] : key;
    }

    /// <summary>生成实例标题： "&lt;模板标题&gt; · W&lt;ww&gt;"。</summary>
    public static string InstanceTitle(string templateTitle, string weekKey) =>
        $"{templateTitle} · {ShortSuffix(weekKey)}";

    /// <summary>
    /// "2026-W37" → 那一周的**周一**。
    ///
    /// 用「当年的 1 月 4 日」做锚点：ISO-8601 规定第 1 周必定包含 1 月 4 日，
    /// 所以 1/4 所在周的周一就是第 1 周的周一，再按周号平移 7 的倍数即可。
    /// 解析失败（json 被手改过）返回 null，不抛异常。
    /// </summary>
    public static DateTime? WeekStart(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        string trimmed = key.Trim();
        int sep = trimmed.IndexOf("-W", StringComparison.OrdinalIgnoreCase);
        if (sep <= 0 || sep + 2 >= trimmed.Length) return null;

        if (!int.TryParse(trimmed[..sep], NumberStyles.None, CultureInfo.InvariantCulture, out int year)) return null;
        if (!int.TryParse(trimmed[(sep + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out int week)) return null;
        if (year < 1 || year > 9999 || week < 1 || week > 53) return null;

        try
        {
            DateTime jan4 = new(year, 1, 4);
            DateTime week1Monday = jan4.AddDays(-(IsoWeekday(jan4) - 1));
            return week1Monday.AddDays((week - 1) * 7);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// 逾期周数：未完成、且 <paramref name="weeklyKey"/> 早于 <paramref name="currentKey"/> 时 &gt; 0。
    ///
    /// 先把两个周键还原成各自的周一再相减 —— 直接比较周号会在跨年时出错
    /// （<c>2026-W53</c> 的下一周是 <c>2027-W01</c>）。
    /// 刻意**不**把未来的周键算成逾期（时钟回拨/未来实例）。
    /// </summary>
    public static int OverdueWeeks(string? weeklyKey, string currentKey)
    {
        if (string.IsNullOrEmpty(weeklyKey)) return 0;

        DateTime? from = WeekStart(weeklyKey);
        DateTime? to = WeekStart(currentKey);
        if (from is null || to is null) return 0;

        int days = (to.Value.Date - from.Value.Date).Days;
        int weeks = days / 7;             // 两个都是周一，差必定是 7 的整数倍
        return weeks > 0 ? weeks : 0;
    }

    /// <summary>「已逾期 N 周」（规格 §15.5）；没逾期返回空串（调用方据此决定是否显示）。</summary>
    public static string OverdueText(string? weeklyKey, string currentKey)
    {
        int weeks = OverdueWeeks(weeklyKey, currentKey);
        return weeks > 0 ? $"已逾期 {weeks} 周" : string.Empty;
    }

    /// <summary>
    /// 带状态的重载：**已完成的任务永不逾期** —— 与 <see cref="DailyKeys.OverdueDays"/>
    /// 的「已完成 → 0」逐条一致（规格 §15.5 说每周实例与每日实例在生命周期上完全一样）。
    /// </summary>
    public static int OverdueWeeks(string? weeklyKey, TaskStatus status, string currentKey) =>
        status == TaskStatus.Done ? 0 : OverdueWeeks(weeklyKey, currentKey);

    /// <summary>带状态的「已逾期 N 周」文案（已完成 → 空串）。</summary>
    public static string OverdueText(string? weeklyKey, TaskStatus status, string currentKey)
    {
        int weeks = OverdueWeeks(weeklyKey, status, currentKey);
        return weeks > 0 ? $"已逾期 {weeks} 周" : string.Empty;
    }
}
