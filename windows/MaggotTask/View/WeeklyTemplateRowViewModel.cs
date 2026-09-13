using MaggotTask.Core;

namespace MaggotTask.View;

/// <summary>
/// 每周任务模板列表里的一行（规格 §15.5 → §12.4 的界面部分）。
///
/// 与 <see cref="DailyTemplateRowViewModel"/> 逐项同构，多一列「星期几」，
/// 逾期单位从「天」换成「周」。
/// </summary>
public sealed class WeeklyTemplateRowViewModel
{
    private readonly IAppHost _host;

    public WeeklyTemplateRowViewModel(
        WeeklyTemplate template,
        IReadOnlyList<TaskItem> instances,
        string weekKey,
        IAppHost host)
    {
        _host = host;
        Template = template;
        ThisWeekInstance = instances.FirstOrDefault(t => t.WeeklyKey == weekKey);
        ThisWeekStatusText = LifecycleEngine.WeeklyInstanceStatusText(ThisWeekInstance);

        string code = ThisWeekInstance is null
            ? "—"
            : (ThisWeekInstance.Status == TaskStatus.Done
                ? "DONE"
                : LifecycleEngine.StageOf(ThisWeekInstance).Code());
        ThisWeekChipText = $"{code} · {ThisWeekStatusText}";

        int weeks = 0;
        foreach (TaskItem t in instances)
        {
            int w = WeeklyKeys.OverdueWeeks(t.WeeklyKey, t.Status, weekKey);
            if (w > weeks) weeks = w;
        }
        OverdueWeeks = weeks;
    }

    public WeeklyTemplate Template { get; }

    public TaskItem? ThisWeekInstance { get; }

    public string Id => Template.Id;
    public string Title => string.IsNullOrWhiteSpace(Template.Title) ? "未命名模板" : Template.Title;
    public bool Enabled => Template.Enabled;

    /// <summary>ISO 星期几 1..7，以及它的中文名。</summary>
    public int Weekday => Template.Weekday;
    public string WeekdayLabel => WeeklyKeys.WeekdayLabel(Template.Weekday);

    public string ThisWeekStatusText { get; }
    public string ThisWeekChipText { get; }
    public int OverdueWeeks { get; }

    public bool HasOverdue => OverdueWeeks > 0;

    /// <summary>「已逾期 N 周」（规格 §15.5）。</summary>
    public string OverdueText => HasOverdue ? $"已逾期 {OverdueWeeks} 周" : string.Empty;

    /// <summary>模板停用后整行弱化（但依然可见 —— 它的实例还在烂）。</summary>
    public double RowOpacity => Template.Enabled ? 1.0 : 0.55;

    public string SummaryText
    {
        get
        {
            var parts = new List<string> { $"本周 {ThisWeekStatusText}" };
            if (HasOverdue) parts.Add(OverdueText);
            if (!Template.Enabled) parts.Add("已停用");
            return string.Join(" · ", parts);
        }
    }

    public RelayCommand ToggleCommand =>
        _toggle ??= new RelayCommand(() => _host.SetWeeklyTemplateEnabled(Id, !Template.Enabled));

    public RelayCommand DeleteCommand =>
        _delete ??= new RelayCommand(() => _host.RemoveWeeklyTemplate(Id));

    private RelayCommand? _toggle;
    private RelayCommand? _delete;
}
