using MaggotTask.Core;
using MaggotTask.Services;

namespace MaggotTask.View;

/// <summary>
/// 视图层与业务层之间的窄接口。
/// 刻意**不**暴露 <see cref="LifecycleEngine"/> 本体 —— 视图只能拿只读快照，
/// 所有写操作都必须走这里的方法，避免界面绕过「标记脏数据 + 刷新」的路径。
/// </summary>
public interface IAppHost
{
    MaggotConfig Config { get; }

    SoundService Sound { get; }

    /// <summary>任务只读快照（克隆，可安全跨线程/跨帧持有）。</summary>
    IReadOnlyList<TaskItem> TaskSnapshot();

    /// <summary>当前任务总数（无需分配快照）。</summary>
    int TaskCount { get; }

    /// <summary>当前苍蝇数量。</summary>
    int FlyCount { get; }

    /// <summary>四阶段 + 战果计数。</summary>
    (int Egg, int Larva, int Pupa, int Fly, int Dead) StageCounts();

    /// <summary>整体平均腐烂度。</summary>
    double AverageRot { get; }

    /// <summary>累计拍死数量。</summary>
    long SquashedCount { get; }

    /// <summary>
    /// 元游戏进度（货币 / 击杀 / 连胜 / 工具 / 勋章）。视图只读它；
    /// 花钱与换装备必须走 <see cref="BuyTool"/> / <see cref="EquipTool"/>。
    /// </summary>
    GameProgress Progress { get; }

    /// <summary>购买工具（成功后自动装备、落盘、刷新界面）。</summary>
    void BuyTool(string toolId);

    /// <summary>装备一件已拥有的工具。</summary>
    void EquipTool(string toolId);

    /// <summary>打开军械库。</summary>
    void OpenShop();

    /// <summary>打开荣誉殿堂（连胜日历 + 勋章墙）。</summary>
    void OpenHonor();

    /// <summary>距离变苍蝇的剩余时间文案。</summary>
    string RemainingText(float rot);

    /// <summary>标记状态已变更，安排一次去抖落盘。</summary>
    void MarkDirty();

    /// <summary>订阅「需要刷新界面」通知；返回取消订阅的句柄。</summary>
    IDisposable SubscribeViewRefresh(Action onRefresh);

    /// <summary>立刻刷新所有已打开界面的绑定（诊断用；正常路径走 200ms 节流）。</summary>
    void SyncViews();

    /// <summary>托盘「显示/隐藏控制面板」菜单项当前文案（诊断用，验证显隐不会脱钩）。</summary>
    string TrayPanelMenuText { get; }

    void TogglePanel();
    void ToggleSwarm();
    void ToggleRunning();
    void ApplyClickThrough();
    void OpenTaskList();

    /// <summary>面板显隐发生变化时调用，用来同步托盘菜单文案（避免开关与实际状态脱钩）。</summary>
    void NotifyPanelVisibilityChanged();

    bool NewTask(string title);
    void ResetAllTasks();
    void ClearAllTasks();
    void Care(string taskId);
    void Complete(string taskId);
    void Delete(string taskId);

    // ---- 每日任务（规格 §12）----

    /// <summary>每日模板只读快照（按 sortOrder 排序）。</summary>
    IReadOnlyList<DailyTemplate> DailyTemplates();

    /// <summary>
    /// 新增模板，并**立刻**生成今天的实例（规格 §12.4）。
    /// <paramref name="spawnedToday"/> = 本次立刻生成的实例数（0 表示已存在或任务数已满）。
    /// </summary>
    DailyTemplate? AddDailyTemplate(string title, out int spawnedToday);

    /// <summary>停用/启用模板（已有实例不受影响）。</summary>
    void SetDailyTemplateEnabled(string templateId, bool enabled);

    /// <summary>删除模板（已有实例保留，退化成普通任务）。</summary>
    void RemoveDailyTemplate(string templateId);

    /// <summary>今天（设备本地日期）的日期键，"YYYY-MM-DD"。</summary>
    string TodayKey { get; }

    /// <summary>某模板今天的实例状态文案：未生成 / 卵 / 蛆 / 蛹 / 苍蝇 / 已完成。</summary>
    string DailyInstanceStatus(DailyTemplate template);

    // ---- 每周任务（规格 §15）----

    /// <summary>每周模板只读快照（按 sortOrder 排序）。</summary>
    IReadOnlyList<WeeklyTemplate> WeeklyTemplates();

    /// <summary>
    /// 新增每周模板（含星期几），今天已经到达该星期就**立刻**生成本周实例（规格 §15.5 → §12.4）。
    /// <paramref name="spawnedThisWeek"/> = 本次立刻生成的实例数（0 = 还没到那天 / 已存在 / 任务数已满）。
    /// </summary>
    WeeklyTemplate? AddWeeklyTemplate(string title, int weekday, out int spawnedThisWeek);

    /// <summary>停用/启用模板（已有实例不受影响）。</summary>
    void SetWeeklyTemplateEnabled(string templateId, bool enabled);

    /// <summary>删除模板（已有实例保留，退化成普通任务）。</summary>
    void RemoveWeeklyTemplate(string templateId);

    /// <summary>本周（设备本地日期）的 ISO 周键，"YYYY-Www"。</summary>
    string ThisWeekKey { get; }

    /// <summary>某模板本周的实例状态文案：未生成 / 卵 / 蛆 / 蛹 / 苍蝇 / 已完成。</summary>
    string WeeklyInstanceStatus(WeeklyTemplate template);

    void ExitApp();

    /// <summary>系统是否要求减少动效。</summary>
    bool ReduceMotion { get; }

    /// <summary>虫群层当前是否可见。</summary>
    bool SwarmVisible { get; }
}
