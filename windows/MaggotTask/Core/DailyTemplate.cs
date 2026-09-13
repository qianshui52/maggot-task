using System.Globalization;
using System.Text.Json.Serialization;

namespace MaggotTask.Core;

/// <summary>
/// 每日任务模板（规格 §3 / §12）。
///
/// 一个「每日任务」不是一个任务，而是一个模板：每天按**设备本地日期**生成一个新的实例，
/// 实例标题带当天日期，然后完全按普通任务的生命周期腐烂。
/// </summary>
public sealed class DailyTemplate
{
    /// <summary>UUID。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    /// <summary>模板标题（实例标题 = "&lt;title&gt; · MM-DD"）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>备注，可为空；生成实例时原样带过去。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>关掉后不再生成新实例（已有实例不受影响）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>epoch millis。</summary>
    public long CreatedAt { get; set; }

    /// <summary>列表排序。</summary>
    public int SortOrder { get; set; }

    public DailyTemplate Clone() => new()
    {
        Id = Id,
        Title = Title,
        Note = Note,
        Enabled = Enabled,
        CreatedAt = CreatedAt,
        SortOrder = SortOrder,
    };

    public override string ToString() =>
        $"DailyTemplate({Id[..Math.Min(8, Id.Length)]} {Title} enabled={Enabled} order={SortOrder})";
}

/// <summary>
/// 每日任务的时间与文案工具。
///
/// ⚠️ 时区：一律用**设备本地日期**，绝不用 UTC。
/// 用户在 UTC+8 晚上 8 点看到的「今天」必须是他的今天。
/// </summary>
public static class DailyKeys
{
    /// <summary>把本地时间戳格式化成 "YYYY-MM-DD"。</summary>
    public static string FromLocal(DateTime local) =>
        local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>把 epoch millis 按其本地时区格式化成 "YYYY-MM-DD"。</summary>
    public static string FromUnixMs(long unixMs) =>
        FromLocal(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime);

    /// <summary>今天的本地日期键。</summary>
    public static string Today() => FromLocal(DateTime.Now);

    /// <summary>取 "YYYY-MM-DD" 里的 "MM-DD" 后缀（实例标题用）。</summary>
    public static string ShortSuffix(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        return key.Length >= 5 ? key[^5..] : key;
    }

    /// <summary>生成实例标题： "&lt;模板标题&gt; · MM-DD"。</summary>
    public static string InstanceTitle(string templateTitle, string key) =>
        $"{templateTitle} · {ShortSuffix(key)}";

    /// <summary>
    /// 解析日期键；失败返回 null（json 被手改过也不至于崩）。
    /// </summary>
    public static DateTime? Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return DateTime.TryParseExact(
            key.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// 逾期天数：未完成、且 <paramref name="dailyKey"/> 早于「今天」时为 &gt; 0。
    ///
    /// 刻意**不**把 <paramref name="dailyKey"/> 晚于今天的实例算成逾期（时钟回拨/未来实例）。
    /// </summary>
    public static int OverdueDays(string? dailyKey, TaskStatus status, string todayKey)
    {
        if (string.IsNullOrEmpty(dailyKey)) return 0;
        if (status == TaskStatus.Done) return 0;

        DateTime? key = Parse(dailyKey);
        DateTime? today = Parse(todayKey);
        if (key is null || today is null) return 0;

        int days = (today.Value.Date - key.Value.Date).Days;
        return days > 0 ? days : 0;
    }

    /// <summary>「已逾期 N 天」；没逾期返回空串（调用方据此决定是否显示）。</summary>
    public static string OverdueText(string? dailyKey, TaskStatus status, string todayKey)
    {
        int days = OverdueDays(dailyKey, status, todayKey);
        return days > 0 ? $"已逾期 {days} 天" : string.Empty;
    }

    /// <summary>
    /// 「放置 X」文案（规格 §14.2 的「放置」行）= now - lastCaredAt。
    /// </summary>
    public static string PlacedText(long lastCaredAt, long now)
    {
        long ms = now - lastCaredAt;
        if (ms < 0) ms = 0;

        double sec = ms / 1000.0;
        if (sec < 60) return $"{Math.Max(0, (int)Math.Round(sec))} 秒";
        if (sec < 3600) return $"{Math.Max(1, (int)Math.Round(sec / 60.0))} 分钟";

        double hours = sec / 3600.0;
        if (hours < 24)
        {
            int h = (int)hours;
            int m = (int)Math.Round((hours - h) * 60.0);
            if (m >= 60) { h++; m = 0; }
            return m == 0 ? $"{h} 小时" : $"{h} 小时 {m} 分";
        }

        double days = hours / 24.0;
        return $"{days:0.#} 天";
    }
}
