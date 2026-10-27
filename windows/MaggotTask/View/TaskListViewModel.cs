using System.Collections.ObjectModel;
using MaggotTask.Core;

namespace MaggotTask.View;

/// <summary>
/// 任务列表视图模型：每日 / 每周模板 + 活跃任务 + 「标本缸」（已击杀汇总）。
///
/// 战果不再逐条列出：清掉多少只都倒进同一口缸里，按阶段分成四格，
/// 悬停看数量与说明。理由——逐条列出 50 个已完成任务既占地方、也没人看第二遍，
/// 而"我这周泡了 12 只苍蝇"这件事才有反馈感。
/// </summary>
public sealed class TaskListViewModel : ViewModelBase
{
    private readonly IAppHost _host;
    private int _total;
    private int _fly;
    private int _squashed;
    private int _overdue;
    private int _weeklyOverdue;

    private int _specimenEgg;
    private int _specimenLarva;
    private int _specimenPupa;
    private int _specimenFly;

    public TaskListViewModel(IAppHost host)
    {
        _host = host;
    }

    /// <summary>未拍死的任务，按腐烂度降序（最烂的最上面 —— 先救它）。</summary>
    public ObservableCollection<TaskRowViewModel> ActiveRows { get; } = new();

    /// <summary>每日任务模板（规格 §12）。</summary>
    public ObservableCollection<DailyTemplateRowViewModel> DailyRows { get; } = new();

    /// <summary>每周任务模板（规格 §15）。</summary>
    public ObservableCollection<WeeklyTemplateRowViewModel> WeeklyRows { get; } = new();

    public string HeaderCountText => $"共 {_total} 个 · 苍蝇 {_fly}";

    /// <summary>标本缸总数（缸盖上那个数字）。</summary>
    public string SpecimenTotalText => $"共 {_specimenEgg + _specimenLarva + _specimenPupa + _specimenFly} 只";

    public int SpecimenEgg => _specimenEgg;
    public int SpecimenLarva => _specimenLarva;
    public int SpecimenPupa => _specimenPupa;
    public int SpecimenFly => _specimenFly;

    /// <summary>四格的悬停说明。数量为 0 时也说明白，而不是显示空白。</summary>
    public string SpecimenEggTip => _specimenEgg == 0
        ? "卵 · 还没泡进去过。趁早清，值 100。"
        : $"卵 · 标本 {_specimenEgg} 只 · 每只 100 货币";

    public string SpecimenLarvaTip => _specimenLarva == 0
        ? "蛆 · 缸里还没有。"
        : $"蛆 · 标本 {_specimenLarva} 只 · 每只 50 货币";

    public string SpecimenPupaTip => _specimenPupa == 0
        ? "蛹 · 缸里还没有。"
        : $"蛹 · 标本 {_specimenPupa} 只 · 每只 25 货币";

    public string SpecimenFlyTip => _specimenFly == 0
        ? "苍蝇 · 还没让它飞起来过。"
        : $"苍蝇 · 标本 {_specimenFly} 只 · 每只 10 货币";

    /// <summary>缸盖上的总说明（整体悬停）。</summary>
    public string SpecimenSummaryTip =>
        $"标本缸 · 累计 {_specimenEgg + _specimenLarva + _specimenPupa + _specimenFly} 只\n" +
        $"卵 {_specimenEgg} · 蛆 {_specimenLarva} · 蛹 {_specimenPupa} · 苍蝇 {_specimenFly}\n" +
        $"（只统计最近保留的 {LifecycleEngine.SquashKeep} 条战果）";

    /// <summary>每日任务区的标题：模板数 + 逾期实例数（规格 §12.1「已逾期 N 天」标记）。</summary>
    public string DailyHeaderText => DailyRows.Count == 0
        ? "每日任务"
        : $"每日任务 · {DailyRows.Count} 个模板" + (_overdue > 0 ? $" · 逾期 {_overdue}" : string.Empty);

    public bool HasNoDaily => DailyRows.Count == 0;

    /// <summary>每周任务区的标题：模板数 + 逾期实例数（规格 §15.5「已逾期 N 周」标记）。</summary>
    public string WeeklyHeaderText => WeeklyRows.Count == 0
        ? "每周任务"
        : $"每周任务 · {WeeklyRows.Count} 个模板"
          + (_weeklyOverdue > 0 ? $" · 逾期 {_weeklyOverdue}" : string.Empty)
          + $" · 本周 {_host.ThisWeekKey}";

    public bool HasNoWeekly => WeeklyRows.Count == 0;

    public string FooterText =>
        _total == 0
            ? "空缸。放一只虫子进去。"
            : $"最烂的在最上面 · 拍死保留最近 {LifecycleEngine.SquashKeep} 条";

    public bool HasNoTasks => ActiveRows.Count == 0;

    /// <summary>重建列表（数据量小，全量重建比差分更新更不容易出错）。</summary>
    public void Refresh()
    {
        IReadOnlyList<TaskItem> tasks = _host.TaskSnapshot();
        string todayKey = _host.TodayKey;
        string weekKey = _host.ThisWeekKey;

        List<TaskItem> active = new();
        List<TaskItem> done = new();

        foreach (TaskItem t in tasks)
        {
            if (t.Status == TaskStatus.Done) done.Add(t);
            else active.Add(t);
        }

        active.Sort((a, b) => b.Rot.CompareTo(a.Rot));
        // 战果：最近的在前
        done.Sort((a, b) => (b.CompletedAt ?? b.CreatedAt).CompareTo(a.CompletedAt ?? a.CreatedAt));
        if (done.Count > LifecycleEngine.SquashKeep)
        {
            done = done.GetRange(0, LifecycleEngine.SquashKeep);
        }

        Sync(ActiveRows, active, todayKey, weekKey);

        // ---- 标本缸：只统计，不列条目 ----
        // 已完成任务的 Rot 保留着完成那一刻的值，用它反推"当初是什么"。
        int egg = 0, larva = 0, pupa = 0, fly = 0;
        foreach (TaskItem t in done)
        {
            switch (LifecycleEngine.StageFromRot(t.Rot))
            {
                case Stage.Egg: egg++; break;
                case Stage.Larva: larva++; break;
                case Stage.Pupa: pupa++; break;
                case Stage.Fly: fly++; break;
            }
        }
        _specimenEgg = egg;
        _specimenLarva = larva;
        _specimenPupa = pupa;
        _specimenFly = fly;

        // ---- 每日模板 ----
        IReadOnlyList<DailyTemplate> templates = _host.DailyTemplates();
        IReadOnlyList<TaskItem> allTasks = tasks;
        DailyRows.Clear();
        int overdue = 0;
        foreach (DailyTemplate t in templates)
        {
            List<TaskItem> instances = new();
            foreach (TaskItem task in allTasks)
            {
                if (string.Equals(task.DailyTemplateId, t.Id, StringComparison.Ordinal)) instances.Add(task);
            }

            DailyTemplateRowViewModel row = new(t, instances, todayKey, _host);
            if (row.HasOverdue) overdue++;
            DailyRows.Add(row);
        }
        _overdue = overdue;
        Raise(nameof(DailyHeaderText));
        Raise(nameof(HasNoDaily));

        // ---- 每周模板（规格 §15）----
        IReadOnlyList<WeeklyTemplate> weeklyTemplates = _host.WeeklyTemplates();
        WeeklyRows.Clear();
        int weeklyOverdue = 0;
        foreach (WeeklyTemplate t in weeklyTemplates)
        {
            List<TaskItem> instances = new();
            foreach (TaskItem task in allTasks)
            {
                if (string.Equals(task.WeeklyTemplateId, t.Id, StringComparison.Ordinal)) instances.Add(task);
            }

            WeeklyTemplateRowViewModel row = new(t, instances, weekKey, _host);
            if (row.HasOverdue) weeklyOverdue++;
            WeeklyRows.Add(row);
        }
        _weeklyOverdue = weeklyOverdue;
        Raise(nameof(WeeklyHeaderText));
        Raise(nameof(HasNoWeekly));

        _total = tasks.Count;
        _fly = _host.FlyCount;
        _squashed = (int)_host.SquashedCount;

        Raise(nameof(HeaderCountText));
        Raise(nameof(FooterText));
        Raise(nameof(HasNoTasks));
        Raise(nameof(SpecimenTotalText));
        Raise(nameof(SpecimenEgg));
        Raise(nameof(SpecimenLarva));
        Raise(nameof(SpecimenPupa));
        Raise(nameof(SpecimenFly));
        Raise(nameof(SpecimenEggTip));
        Raise(nameof(SpecimenLarvaTip));
        Raise(nameof(SpecimenPupaTip));
        Raise(nameof(SpecimenFlyTip));
        Raise(nameof(SpecimenSummaryTip));
    }

    /// <summary>就地更新：同一批 id 不重建，避免列表每 500ms 闪一次。</summary>
    private void Sync(ObservableCollection<TaskRowViewModel> target, List<TaskItem> source,
        string todayKey, string weekKey)
    {
        bool sameShape = target.Count == source.Count;
        if (sameShape)
        {
            for (int i = 0; i < source.Count; i++)
            {
                if (target[i].Id != source[i].Id)
                {
                    sameShape = false;
                    break;
                }
            }
        }

        if (sameShape)
        {
            for (int i = 0; i < source.Count; i++)
            {
                // 阶段/逾期标记变了就重建行，避免每 500ms 刷新把列表闪一遍。
                if (target[i].Stage != LifecycleEngine.StageOf(source[i])
                    || target[i].IsOverdueNow(todayKey, weekKey) != target[i].IsOverdue)
                {
                    target[i] = new TaskRowViewModel(source[i], _host, todayKey, weekKey);
                }
            }
            return;
        }

        target.Clear();
        foreach (TaskItem t in source)
        {
            target.Add(new TaskRowViewModel(t, _host, todayKey, weekKey));
        }
    }
}
