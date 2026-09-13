using System.Windows.Media;
using MaggotTask.Core;

namespace MaggotTask.View;

/// <summary>
/// 任务列表的一行（设计系统 §8）。
/// [阶段色竖条 3px] 标题 / [阶段 chip] [rot 进度条] [剩余时间] / 右侧三个动作。
/// 阶段信息不只靠颜色传达 —— chip 里始终有文字。
/// </summary>
public sealed class TaskRowViewModel
{
    public TaskRowViewModel(TaskItem task, IAppHost host, string? todayKey = null, string? weekKey = null)
    {
        Task = task;
        Stage = LifecycleEngine.StageOf(task);
        Host = host;
        TodayKey = todayKey ?? DailyKeys.Today();
        WeekKey = weekKey ?? WeeklyKeys.Today();
        OverdueDays = DailyKeys.OverdueDays(task.DailyKey, task.Status, TodayKey);
        OverdueWeeks = WeeklyKeys.OverdueWeeks(task.WeeklyKey, task.Status, WeekKey);

        CareCommand = new RelayCommand(() => host.Care(task.Id));
        CompleteCommand = new RelayCommand(() => host.Complete(task.Id));
        DeleteCommand = new RelayCommand(() => host.Delete(task.Id));
    }

    public IAppHost Host { get; }
    public TaskItem Task { get; }
    public Stage Stage { get; }
    public string TodayKey { get; }

    /// <summary>本行渲染时用的 ISO 周键（用于「已逾期 N 周」的判定）。</summary>
    public string WeekKey { get; }

    public string Id => Task.Id;
    public string Title => string.IsNullOrWhiteSpace(Task.Title) ? "未命名任务" : Task.Title;

    /// <summary>阶段 chip：11px 等宽大写 + 阶段色圆点 + 阶段名。</summary>
    public string StageCode => Stage.Code();
    public string StageLabel => Stage.Label();

    /// <summary>阶段色（chip 圆点 / 左侧竖条）。</summary>
    public Brush StageBrush => Render.Palette.Frozen(new SolidColorBrush(Render.Palette.StageColor(Stage)));

    public float Rot => Task.Rot;

    public string Remaining => Host.RemainingText(Task.Rot);

    /// <summary>是不是每日实例（规格 §12.3：生命周期与普通任务完全一样，只是来源不同）。</summary>
    public bool IsDaily => !string.IsNullOrEmpty(Task.DailyTemplateId) || !string.IsNullOrEmpty(Task.DailyKey);

    /// <summary>是不是每周实例（规格 §15.2 / §15.5）。</summary>
    public bool IsWeekly => !string.IsNullOrEmpty(Task.WeeklyTemplateId) || !string.IsNullOrEmpty(Task.WeeklyKey);

    /// <summary>逾期天数；0 = 没逾期。</summary>
    public int OverdueDays { get; }

    /// <summary>逾期周数；0 = 没逾期（规格 §15.5 的「已逾期 N 周」）。</summary>
    public int OverdueWeeks { get; }

    public bool IsOverdue => OverdueDays > 0 || OverdueWeeks > 0;

    /// <summary>
    /// 逾期标记文案：每周实例用「已逾期 N 周」（§15.5），每日实例用「已逾期 N 天」（§12.1）。
    /// 未逾期时为空串（XAML 里整块胶囊也随之收起）。
    /// </summary>
    public string OverdueText => OverdueWeeks > 0
        ? $"已逾期 {OverdueWeeks} 周"
        : (OverdueDays > 0 ? $"已逾期 {OverdueDays} 天" : string.Empty);

    /// <summary>按今天的日期键 / 本周的周键重算是否逾期 —— 用于决定行要不要重建。</summary>
    public bool IsOverdueNow(string todayKey, string weekKey) =>
        DailyKeys.OverdueDays(Task.DailyKey, Task.Status, todayKey) > 0
        || WeeklyKeys.OverdueWeeks(Task.WeeklyKey, Task.Status, weekKey) > 0;

    /// <summary>进度 + 代数 + 基因，作为「实验记录」式副标题。</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (IsDaily) parts.Add("每日");
            if (IsWeekly) parts.Add("每周");
            parts.Add($"进度 {Task.Progress}%");
            parts.Add($"ROT {Task.Rot:0.00}");
            if (Task.Generation > 0)
            {
                parts.Add($"第 {Task.Generation} 代");
                parts.Add($"基因 ×{Task.Gene:0.0}");
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>已拍死的行弱化显示。</summary>
    public double RowOpacity => Task.Status == TaskStatus.Done ? 0.55 : 1.0;

    public bool IsDone => Task.Status == TaskStatus.Done;

    public RelayCommand CareCommand { get; }
    public RelayCommand CompleteCommand { get; }
    public RelayCommand DeleteCommand { get; }
}
