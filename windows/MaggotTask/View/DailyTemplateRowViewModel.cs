using MaggotTask.Core;

namespace MaggotTask.View;

/// <summary>
/// 每日任务模板列表里的一行（规格 §12.4 / §12.5 的界面部分）。
/// 每行：启用开关 + 模板名 + 今天的实例状态 + 删除。
/// 另外汇总该模板的逾期实例，给出「已逾期 N 天」标记。
/// </summary>
public sealed class DailyTemplateRowViewModel
{
    private readonly IAppHost _host;

    public DailyTemplateRowViewModel(
        DailyTemplate template,
        IReadOnlyList<TaskItem> instances,
        string todayKey,
        IAppHost host)
    {
        _host = host;
        Template = template;
        TodayInstance = instances.FirstOrDefault(t => t.DailyKey == todayKey);
        TodayStatusText = LifecycleEngine.DailyInstanceStatusText(TodayInstance);

        string code = TodayInstance is null
            ? "—"
            : (TodayInstance.Status == TaskStatus.Done
                ? "DONE"
                : LifecycleEngine.StageOf(TodayInstance).Code());
        TodayChipText = $"{code} · {TodayStatusText}";

        int overdue = 0;
        foreach (TaskItem t in instances)
        {
            int days = DailyKeys.OverdueDays(t.DailyKey, t.Status, todayKey);
            if (days > overdue) overdue = days;
        }
        OverdueDays = overdue;
    }

    public DailyTemplate Template { get; }

    public TaskItem? TodayInstance { get; }

    public string Id => Template.Id;
    public string Title => string.IsNullOrWhiteSpace(Template.Title) ? "未命名模板" : Template.Title;
    public bool Enabled => Template.Enabled;
    public string TodayStatusText { get; }
    public string TodayChipText { get; }
    public int OverdueDays { get; }

    public bool HasOverdue => OverdueDays > 0;
    public string OverdueText => HasOverdue ? $"已逾期 {OverdueDays} 天" : string.Empty;

    /// <summary>模板停用后整行弱化（但依然可见 —— 它的实例还在烂）。</summary>
    public double RowOpacity => Template.Enabled ? 1.0 : 0.55;

    public string SummaryText
    {
        get
        {
            var parts = new List<string> { $"今日 {TodayStatusText}" };
            if (HasOverdue) parts.Add(OverdueText);
            if (!Template.Enabled) parts.Add("已停用");
            return string.Join(" · ", parts);
        }
    }

    public RelayCommand ToggleCommand =>
        _toggle ??= new RelayCommand(() => _host.SetDailyTemplateEnabled(Id, !Template.Enabled));

    public RelayCommand DeleteCommand =>
        _delete ??= new RelayCommand(() => _host.RemoveDailyTemplate(Id));

    private RelayCommand? _toggle;
    private RelayCommand? _delete;
}
