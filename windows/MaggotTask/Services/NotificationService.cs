using System.Diagnostics;
using MaggotTask.Core;

namespace MaggotTask.Services;

/// <summary>
/// 通知调度。规格第 9 节：阶段事件里只有「变成苍蝇」推通知；
/// 启动时对「已经是苍蝇」的任务推一条汇总（避免每次启动炸一堆通知）。
/// </summary>
public sealed class NotificationService
{
    private readonly TrayService _tray;
    private readonly Dictionary<string, long> _lastNotifyAt = new();
    private const long NotifyCooldownMs = 30_000;

    public NotificationService(TrayService tray)
    {
        _tray = tray;
    }

    public bool Enabled { get; set; } = true;

    /// <summary>消费一批引擎事件，按需要推通知。</summary>
    public void Handle(IEnumerable<EngineEvent> events)
    {
        if (!Enabled) return;

        foreach (EngineEvent e in events)
        {
            switch (e.Type)
            {
                case EngineEventType.BecameFly:
                    if (Throttle(e.TaskId))
                    {
                        _tray.Notify(
                            "有虫子变苍蝇了",
                            $"「{Short(e.Title)}」已经烂成苍蝇，要跑掉了。",
                            warning: true);
                    }
                    break;

                case EngineEventType.ApproachingFly:
                    if (Throttle(e.TaskId))
                    {
                        _tray.Notify(
                            "虫子快变苍蝇了",
                            $"「{Short(e.Title)}」腐烂度已过预警线，再不管就飞了。",
                            warning: false);
                    }
                    break;

                case EngineEventType.Bred:
                    // 繁殖不推动通知（会瞬间刷屏），只在虫群层体现。
                    break;
            }
        }
    }

    /// <summary>启动时的汇总通知。</summary>
    public void SummarizeStartup(IReadOnlyList<TaskItem> flies, double offlineSeconds)
    {
        if (!Enabled) return;
        if (flies.Count == 0) return;

        string names = string.Join("、", flies.Take(3).Select(f => Short(f.Title)));
        string more = flies.Count > 3 ? $" 等 {flies.Count} 只" : string.Empty;
        string offline = offlineSeconds >= 60
            ? $"（离开期间补算了 {offlineSeconds / 60.0:0.#} 分钟的腐烂）"
            : string.Empty;

        _tray.Notify(
            $"已有 {flies.Count} 只苍蝇",
            $"{names}{more} 已经烂成苍蝇{offline}。两只会开始交配产卵。",
            warning: true);
    }

    private bool Throttle(string taskId)
    {
        long now = LifecycleEngine.NowMs();
        if (_lastNotifyAt.TryGetValue(taskId, out long last) && now - last < NotifyCooldownMs)
        {
            return false;
        }
        _lastNotifyAt[taskId] = now;
        return true;
    }

    private static string Short(string title)
    {
        if (string.IsNullOrEmpty(title)) return "未命名任务";
        return title.Length <= 14 ? title : title[..14] + "…";
    }
}
