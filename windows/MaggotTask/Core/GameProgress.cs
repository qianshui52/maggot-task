using System.Globalization;
using System.Text.Json.Serialization;

namespace MaggotTask.Core;

/// <summary>一天的战绩。日历热力图与连胜判定都读它。</summary>
public sealed class DayLog
{
    /// <summary>当天击杀数。</summary>
    public int Kills { get; set; }

    /// <summary>当天完成的任务数（含击杀；任务列表里点「完成」也算）。</summary>
    public int Completed { get; set; }

    /// <summary>当天赚到的货币。</summary>
    public int Coins { get; set; }

    public int Egg { get; set; }
    public int Larva { get; set; }
    public int Pupa { get; set; }
    public int Fly { get; set; }

    /// <summary>这一天算不算「打卡」：完成过任务或击杀过虫子。</summary>
    [JsonIgnore]
    public bool Active => Kills > 0 || Completed > 0;
}

/// <summary>一枚勋章的定义。纯数据 + 一个取值函数，进度永远从真实计数派生。</summary>
/// <param name="Id">稳定 id（落盘用）。</param>
/// <param name="Name">名称。</param>
/// <param name="Description">达成条件的自然语言描述。</param>
/// <param name="Glyph">图标字形（用字体里的几何符号，避免位图素材）。</param>
/// <param name="Target">目标值。</param>
/// <param name="Unit">单位文案（只 / 天 / 次）。</param>
/// <param name="Measure">从进度对象里取当前值；null 表示用 <see cref="GameProgress.MedalValue"/> 的特殊规则。</param>
public sealed record MedalDef(
    string Id,
    string Name,
    string Description,
    string Glyph,
    int Target,
    string Unit,
    Func<GameProgress, int> Measure);

/// <summary>
/// 元游戏进度：货币、击杀统计、连胜、连胜日历、工具、勋章。
///
/// 设计原则（这是"各系统数值联动一致"的关键）：
///   · 只有这里**写入**计数；商店、勋章墙、日历、控制面板只**读**同一份字段。
///   · 勋章不单独维护"已达成"布尔，而是每次从计数派生 —— 计数改不了，勋章就不会说谎。
///   · 连胜由「活跃日」推导；活跃日 = 当天击杀 &gt; 0 或完成任务 &gt; 0。
///   · 工具不改变奖励；奖励只由阶段决定。进阶层数只影响一次能清几只。
/// </summary>
public sealed class GameProgress
{
    /// <summary>当前货币余额。</summary>
    public int Coins { get; set; }

    /// <summary>累计赚到（含已花掉）。</summary>
    public int TotalCoinsEarned { get; set; }

    /// <summary>累计花掉。</summary>
    public int TotalSpent { get; set; }

    public int TotalKills { get; set; }
    public int KillsEgg { get; set; }
    public int KillsLarva { get; set; }
    public int KillsPupa { get; set; }
    public int KillsFly { get; set; }

    /// <summary>累计完成的任务数（击杀 + 任务列表完成）。</summary>
    public int TasksCompleted { get; set; }

    /// <summary>当前连胜天数。</summary>
    public int CurrentStreak { get; set; }

    /// <summary>历史最长连胜。</summary>
    public int BestStreak { get; set; }

    /// <summary>最近一个活跃日（"yyyy-MM-dd"，本地日期）。空 = 从未活跃。</summary>
    public string LastActiveDate { get; set; } = string.Empty;

    /// <summary>最近 400 天的每日战绩（键 = "yyyy-MM-dd"）。</summary>
    public Dictionary<string, DayLog> Days { get; set; } = new(StringComparer.Ordinal);

    /// <summary>已拥有的工具 id。</summary>
    public List<string> OwnedTools { get; set; } = new() { ToolCatalog.SwatterId };

    /// <summary>当前装备的工具 id。</summary>
    public string EquippedTool { get; set; } = ToolCatalog.SwatterId;

    /// <summary>已解锁勋章：id → 解锁日期。</summary>
    public Dictionary<string, string> UnlockedMedals { get; set; } = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------
    // 派生值
    // ------------------------------------------------------------------

    /// <summary>当前装备的工具（永远返回有效对象）。</summary>
    [JsonIgnore]
    public ToolDef EquippedToolDef => ToolCatalog.Get(EquippedTool);

    /// <summary>
    /// 热度等级 0–5，由当前连胜换算（多邻国式的"火苗越来越旺"）。
    /// 0 = 熄灭 · 1 = 1–2 天 · 2 = 3–6 天 · 3 = 7–13 天 · 4 = 14–29 天 · 5 = ≥30 天。
    /// </summary>
    [JsonIgnore]
    public int HeatLevel => CurrentStreak switch
    {
        >= 30 => 5,
        >= 14 => 4,
        >= 7 => 3,
        >= 3 => 2,
        >= 1 => 1,
        _ => 0,
    };

    /// <summary>热度等级文案。</summary>
    [JsonIgnore]
    public string HeatLabel => HeatLevel switch
    {
        5 => "燎原",
        4 => "烈焰",
        3 => "旺火",
        2 => "小火",
        1 => "余温",
        _ => "熄灭",
    };

    /// <summary>今天的战绩（不存在时返回空对象，不写入）。</summary>
    public DayLog DayOrEmpty(string dateKey) =>
        Days.TryGetValue(dateKey, out DayLog? log) ? log : new DayLog();

    /// <summary>今天是否已经打卡。</summary>
    public bool IsActiveOn(string dateKey) => DayOrEmpty(dateKey).Active;

    // ------------------------------------------------------------------
    // 归一化（读档后必须调用）
    // ------------------------------------------------------------------

    /// <summary>把被手改 / 旧版本 / null 的字段拉回合法状态。幂等。</summary>
    public void Normalize()
    {
        if (Coins < 0) Coins = 0;
        if (TotalCoinsEarned < Coins) TotalCoinsEarned = Coins;
        if (TotalSpent < 0) TotalSpent = 0;
        if (TotalKills < 0) TotalKills = 0;
        if (KillsEgg < 0) KillsEgg = 0;
        if (KillsLarva < 0) KillsLarva = 0;
        if (KillsPupa < 0) KillsPupa = 0;
        if (KillsFly < 0) KillsFly = 0;
        if (TasksCompleted < 0) TasksCompleted = 0;
        if (CurrentStreak < 0) CurrentStreak = 0;
        if (BestStreak < CurrentStreak) BestStreak = CurrentStreak;
        LastActiveDate ??= string.Empty;

        Days ??= new Dictionary<string, DayLog>(StringComparer.Ordinal);
        UnlockedMedals ??= new Dictionary<string, string>(StringComparer.Ordinal);
        OwnedTools ??= new List<string>();
        if (!OwnedTools.Contains(ToolCatalog.SwatterId, StringComparer.Ordinal))
        {
            OwnedTools.Insert(0, ToolCatalog.SwatterId);
        }
        // 只保留目录里认识的 id（改过目录之后旧存档不炸）
        for (int i = OwnedTools.Count - 1; i >= 0; i--)
        {
            if (ToolCatalog.Get(OwnedTools[i]).Id != OwnedTools[i]) OwnedTools.RemoveAt(i);
        }
        if (!Owns(EquippedTool)) EquippedTool = ToolCatalog.SwatterId;

        PruneDays(400);
    }

    /// <summary>只保留最近 N 天，防止日历无限膨胀。</summary>
    public void PruneDays(int keep)
    {
        if (Days.Count <= keep) return;

        List<KeyValuePair<string, DayLog>> ordered = Days.ToList();
        ordered.Sort((a, b) => string.CompareOrdinal(b.Key, a.Key));  // 日期键可直接字典序排
        for (int i = keep; i < ordered.Count; i++) Days.Remove(ordered[i].Key);
    }

    // ------------------------------------------------------------------
    // 记账：唯一写入计数的入口
    // ------------------------------------------------------------------

    /// <summary>
    /// 登记一次击杀。返回本次获得的货币。
    /// 只有真正完成了一个任务之后才应调用它（避免"点空"刷钱）。
    /// </summary>
    public int RegisterKill(Stage stage, string dateKey)
    {
        int reward = ToolCatalog.RewardFor(stage);

        Coins += reward;
        TotalCoinsEarned += reward;
        TotalKills++;
        TasksCompleted++;

        switch (stage)
        {
            case Stage.Egg: KillsEgg++; break;
            case Stage.Larva: KillsLarva++; break;
            case Stage.Pupa: KillsPupa++; break;
            case Stage.Fly: KillsFly++; break;
        }

        DayLog log = Day(dateKey);
        log.Kills++;
        log.Completed++;
        log.Coins += reward;
        switch (stage)
        {
            case Stage.Egg: log.Egg++; break;
            case Stage.Larva: log.Larva++; break;
            case Stage.Pupa: log.Pupa++; break;
            case Stage.Fly: log.Fly++; break;
        }

        TouchDay(dateKey);
        return reward;
    }

    private DayLog Day(string dateKey)
    {
        if (!Days.TryGetValue(dateKey, out DayLog? log))
        {
            log = new DayLog();
            Days[dateKey] = log;
        }
        return log;
    }

    /// <summary>
    /// 把某天标记为活跃日，并推进连胜。
    /// 同一天重复调用是幂等的。
    /// </summary>
    public void TouchDay(string dateKey)
    {
        if (string.Equals(LastActiveDate, dateKey, StringComparison.Ordinal)) return;

        // 先把"已经断掉的连胜"清零，再判断是不是接着昨天
        RefreshStreak(dateKey);

        bool continues = TryParseKey(LastActiveDate, out DateOnly last)
                         && TryParseKey(dateKey, out DateOnly today)
                         && today.DayNumber - last.DayNumber == 1;

        CurrentStreak = continues ? CurrentStreak + 1 : 1;
        LastActiveDate = dateKey;
        if (CurrentStreak > BestStreak) BestStreak = CurrentStreak;
    }

    /// <summary>
    /// 按"今天"校正连胜：昨天活跃、今天还没活跃 → 连胜仍然有效（还没到判定时刻）；
    /// 昨天之前活跃 → 已经断了，清零。**不清空 BestStreak**。
    /// </summary>
    public void RefreshStreak(string todayKey)
    {
        if (string.IsNullOrEmpty(LastActiveDate)) return;
        if (string.Equals(LastActiveDate, todayKey, StringComparison.Ordinal)) return;

        if (!TryParseKey(LastActiveDate, out DateOnly last) ||
            !TryParseKey(todayKey, out DateOnly today))
        {
            return;
        }

        int gap = today.DayNumber - last.DayNumber;
        if (gap > 1) CurrentStreak = 0;
        // gap <= 0（时钟被往回调）时不动，避免系统时间改动直接清空连胜
    }

    /// <summary>解析 "yyyy-MM-dd"（只认本地日期键，不做时区换算）。</summary>
    public static bool TryParseKey(string key, out DateOnly date) =>
        DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);

    // ------------------------------------------------------------------
    // 商店
    // ------------------------------------------------------------------

    public bool Owns(string? toolId) =>
        !string.IsNullOrEmpty(toolId) && OwnedTools.Contains(toolId, StringComparer.Ordinal);

    /// <summary>购买。失败时给出可直接展示给用户的原因。</summary>
    public bool Buy(string toolId, out string message)
    {
        ToolDef tool = ToolCatalog.Get(toolId);
        if (tool.Price <= 0)
        {
            message = "这件工具已经在你手上了。";
            return false;
        }
        if (Owns(tool.Id))
        {
            message = $"已经拥有「{tool.Name}」，不用重复购买。";
            return false;
        }
        if (Coins < tool.Price)
        {
            message = $"还差 {tool.Price - Coins} 货币。";
            return false;
        }

        Coins -= tool.Price;
        TotalSpent += tool.Price;
        OwnedTools.Add(tool.Id);
        EquippedTool = tool.Id;
        message = $"已购入「{tool.Name}」并自动装备。";
        return true;
    }

    /// <summary>装备一件已拥有的工具。</summary>
    public bool Equip(string toolId)
    {
        if (!Owns(toolId)) return false;
        EquippedTool = toolId;
        return true;
    }

    // ------------------------------------------------------------------
    // 勋章
    // ------------------------------------------------------------------

    /// <summary>勋章目录（10 枚，全部与打卡 / 击杀相关）。</summary>
    public static readonly IReadOnlyList<MedalDef> MedalCatalog = new[]
    {
        new MedalDef("first-swat", "第一拍", "击杀你的第一只虫子。", "✦", 1, "只",
            p => p.TotalKills),
        new MedalDef("egg-breaker", "破壳者", "累计击杀 10 只卵。", "◍", 10, "只",
            p => p.KillsEgg),
        new MedalDef("larva-reaper", "蛆之克星", "累计击杀 25 只蛆。", "≈", 25, "只",
            p => p.KillsLarva),
        new MedalDef("fly-hunter", "灭蝇专家", "累计击杀 50 只苍蝇。", "✕", 50, "只",
            p => p.KillsFly),
        new MedalDef("all-stages", "四象皆斩", "卵、蛆、蛹、苍蝇各击杀 10 只。", "❖", 10, "只",
            p => Math.Min(Math.Min(p.KillsEgg, p.KillsLarva), Math.Min(p.KillsPupa, p.KillsFly))),
        new MedalDef("streak-3", "三日不辍", "连续打卡 3 天。", "△", 3, "天",
            p => p.BestStreak),
        new MedalDef("streak-7", "七日之约", "连续打卡 7 天。", "◇", 7, "天",
            p => p.BestStreak),
        new MedalDef("streak-30", "月度铁人", "连续打卡 30 天。", "⬢", 30, "天",
            p => p.BestStreak),
        new MedalDef("hundred", "百人斩", "累计击杀 100 只虫子。", "百", 100, "只",
            p => p.TotalKills),
        new MedalDef("thousand", "千虫之主", "累计击杀 1000 只虫子。", "千", 1000, "只",
            p => p.TotalKills),
    };

    /// <summary>某枚勋章的当前进度值。</summary>
    public int MedalValue(MedalDef medal) => Math.Max(0, medal.Measure(this));

    /// <summary>某枚勋章是否已达成（按当前计数实时判定）。</summary>
    public bool MedalReached(MedalDef medal) => MedalValue(medal) >= medal.Target;

    /// <summary>某枚勋章是否已登记解锁。</summary>
    public bool MedalUnlocked(MedalDef medal) =>
        UnlockedMedals.ContainsKey(medal.Id);

    /// <summary>进度 0–1。</summary>
    public double MedalProgress(MedalDef medal)
    {
        if (medal.Target <= 0) return 1.0;
        return Math.Clamp((double)MedalValue(medal) / medal.Target, 0.0, 1.0);
    }

    /// <summary>
    /// 扫描全部勋章，把刚达成的登记下来。返回本次新解锁的列表（用于弹提示）。
    /// </summary>
    public List<MedalDef> EvaluateMedals(string dateKey)
    {
        List<MedalDef> fresh = new();
        foreach (MedalDef m in MedalCatalog)
        {
            if (MedalUnlocked(m) || !MedalReached(m)) continue;
            UnlockedMedals[m.Id] = dateKey;
            fresh.Add(m);
        }
        return fresh;
    }

    /// <summary>
    /// 已解锁枚数。**按达成条件实时数**，不是数 <see cref="UnlockedMedals"/> 里的键 ——
    /// 这样"标题上的数字"和"墙上亮着的卡片"永远是同一件事。
    /// </summary>
    public int UnlockedMedalCount
    {
        get
        {
            int n = 0;
            foreach (MedalDef m in MedalCatalog)
            {
                if (MedalReached(m)) n++;
            }
            return n;
        }
    }
}
