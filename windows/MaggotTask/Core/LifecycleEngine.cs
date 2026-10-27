namespace MaggotTask.Core;

/// <summary>
/// 生命周期引擎 —— 纯逻辑，零 UI 依赖，可单元测试。
/// 逐条实现 docs/lifecycle-spec.md（常量、公式、事件、离线补算）。
/// </summary>
public sealed class LifecycleEngine
{
    // ------------------------------------------------------------------
    // 2. 常量（两端必须一致）
    // ------------------------------------------------------------------

    /// <summary>腐烂度上限。</summary>
    public const float RotMax = 1.20f;

    /// <summary>卵 → 蛆。</summary>
    public const float HatchT = 0.18f;

    /// <summary>→ 蛹。</summary>
    public const float PupaT = 0.50f;

    /// <summary>→ 苍蝇。</summary>
    public const float FlyT = 0.78f;

    /// <summary>预警线（推通知）：FLY_T - 0.10。</summary>
    public const float ApproachT = FlyT - 0.10f; // 0.68

    /// <summary>超过此值仅影响动画强度。</summary>
    public const float RotMaxSafe = 1.00f;

    /// <summary>「我去做了」最大倒扣腐烂度。</summary>
    public const float CareRelief = 0.22f;

    /// <summary>恢复满额效果所需间隔（秒）。</summary>
    public const double CareCooldownSec = 20.0;

    /// <summary>冷却未满时的最低效果系数。</summary>
    public const float CareMinFactor = 0.35f;

    /// <summary>每次「我去做了」最少推进的进度。</summary>
    public const int CareProgressMin = 4;

    /// <summary>最多推进的进度。</summary>
    public const int CareProgressMax = 12;

    /// <summary>每繁殖一代，腐烂速度 +10%。</summary>
    public const float GeneStep = 0.10f;

    /// <summary>基因系数上限。</summary>
    public const float GeneMax = 2.20f;

    /// <summary>重新打开时最多补算 45 分钟的腐烂。</summary>
    public const double OfflineCapMin = 45.0;

    /// <summary>前台 / 可见时的 tick 频率。</summary>
    public const int TickHz = 60;

    /// <summary>悬浮层关闭时降频省电。</summary>
    public const int IdleTickHz = 10;

    /// <summary>单帧最多推进 0.5 秒，防止休眠后跳变。</summary>
    public const double MaxDt = 0.50;

    /// <summary>已拍死任务在「战果」列表中的保留上限。</summary>
    public const int SquashKeep = 50;

    // ------------------------------------------------------------------
    // 状态
    // ------------------------------------------------------------------

    private readonly List<TaskItem> _tasks = new();
    private readonly Dictionary<string, TaskItem> _index = new(StringComparer.Ordinal);
    private readonly List<DailyTemplate> _dailyTemplates = new();
    private readonly List<WeeklyTemplate> _weeklyTemplates = new();
    private readonly object _gate = new();
    private readonly Random _rng;

    /// <summary>繁殖累积器（秒 × rotSpeed）。</summary>
    private double _breedAccum;

    /// <summary>累计拍死数量。</summary>
    private long _squashedCount;

    public LifecycleEngine(MaggotConfig? config = null, int? randomSeed = null)
    {
        Config = config ?? new MaggotConfig();
        Config.Normalize();
        _rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
    }

    public MaggotConfig Config { get; }

    /// <summary>本帧产生的事件。调用方在 <see cref="Tick"/> 之后消费并清空。</summary>
    public List<EngineEvent> Events { get; } = new();

    /// <summary>累计拍死数量。</summary>
    public long SquashedCount
    {
        get { lock (_gate) { return _squashedCount; } }
    }

    /// <summary>最近一次离线补算实际使用的秒数（0 表示没补）。</summary>
    public double LastOfflineCatchUpSeconds { get; private set; }

    /// <summary>
    /// 快照式任务列表：返回克隆副本，UI 拿到之后不会被引擎线程改写，
    /// 也不会误改引擎状态（修改请走 Care / Complete / RemoveTask / ResetAll）。
    /// </summary>
    public IReadOnlyList<TaskItem> Snapshot()
    {
        lock (_gate)
        {
            TaskItem[] copy = new TaskItem[_tasks.Count];
            for (int i = 0; i < _tasks.Count; i++) copy[i] = _tasks[i].Clone();
            return copy;
        }
    }

    /// <summary>内部集合的只读视图（仅限同线程使用，UI 请用 <see cref="Snapshot"/>）。</summary>
    public IReadOnlyList<TaskItem> TasksView
    {
        get { lock (_gate) { return _tasks; } }
    }

    public int Count { get { lock (_gate) { return _tasks.Count; } } }

    public int CountOf(Stage stage)
    {
        lock (_gate)
        {
            int n = 0;
            foreach (TaskItem t in _tasks)
            {
                if (StageOf(t) == stage) n++;
            }
            return n;
        }
    }

    public int FlyCount => CountOf(Stage.Fly);

    public double AverageRot
    {
        get
        {
            lock (_gate)
            {
                if (_tasks.Count == 0) return 0.0;
                double sum = 0.0;
                foreach (TaskItem t in _tasks) sum += t.Rot;
                return sum / _tasks.Count;
            }
        }
    }

    // ------------------------------------------------------------------
    // 4. 腐烂推进 / 阶段推导
    // ------------------------------------------------------------------

    /// <summary>阶段腐烂倍率。</summary>
    public static double StageMultiplier(Stage stage) => stage switch
    {
        Stage.Egg => 0.70,
        Stage.Larva => 1.00,
        Stage.Pupa => 1.35,
        Stage.Fly => 1.60,
        _ => 0.0, // DEAD 不推进
    };

    /// <summary>基因系数。</summary>
    public static float GeneFor(int generation) =>
        Math.Min(1f + GeneStep * generation, GeneMax);

    /// <summary>
    /// 阶段推导。注意顺序：DONE → FLY → PUPA → LARVA(rot) → LARVA(DOING) → EGG。
    /// DOING 的判断放在最后，保证点「我去做了」立刻变蛆，
    /// 但已烂到蛹/苍蝇的不会因为点一下就退回蛆。
    /// </summary>
    public static Stage StageOf(TaskItem task)
    {
        if (task.Status == TaskStatus.Done) return Stage.Dead;
        if (task.Rot >= FlyT) return Stage.Fly;
        if (task.Rot >= PupaT) return Stage.Pupa;
        if (task.Rot >= HatchT) return Stage.Larva;
        if (task.Status == TaskStatus.Doing) return Stage.Larva;
        return Stage.Egg;
    }

    /// <summary>
    /// 只按腐烂度反推阶段，忽略状态。
    ///
    /// 用途：已完成的任务 <see cref="StageOf"/> 一律返回 <see cref="Stage.Dead"/>，
    /// 但「标本缸」需要知道它**当初是被当成什么打死的**。完成时只改了 Status/Progress，
    /// <see cref="TaskItem.Rot"/> 原样保留，所以能用它还原。
    ///
    /// 唯一的偏差：一个 status=Doing 但 rot&lt;HatchT 的任务，当时显示为蛆，
    /// 这里会算成卵。概率很低（Doing 会加速腐烂），且只影响标本分类不影响任何数值。
    /// </summary>
    public static Stage StageFromRot(float rot)
    {
        if (rot >= FlyT) return Stage.Fly;
        if (rot >= PupaT) return Stage.Pupa;
        if (rot >= HatchT) return Stage.Larva;
        return Stage.Egg;
    }

    /// <summary>每秒腐烂量（规格第 4 节）。</summary>
    public static double RotRatePerSecond(TaskItem task, MaggotConfig config)
    {
        double mult = StageMultiplier(StageOf(task));
        return (1.0 / (config.BaseRotMinutes * 60.0)) * config.RotSpeed * task.Gene * mult;
    }

    /// <summary>在给定秒数上的腐烂增量。</summary>
    public static double RotDelta(TaskItem task, MaggotConfig config, double dt)
        => RotRatePerSecond(task, config) * dt;

    /// <summary>距离变成苍蝇还需多少秒（已腐烂到苍蝇返回 0）。</summary>
    public double SecondsToFly(TaskItem task)
    {
        if (StageOf(task) == Stage.Fly) return 0.0;
        double rate = RotRatePerSecond(task, Config);
        if (rate <= 0) return double.PositiveInfinity;
        return (FlyT - task.Rot) / rate;
    }

    // ------------------------------------------------------------------
    // 主循环
    // ------------------------------------------------------------------

    /// <summary>
    /// 推进 <paramref name="dt"/> 秒。dt 会被 clamp 到 <see cref="MaxDt"/>。
    /// </summary>
    public void Tick(double dt, long now)
    {
        if (dt <= 0) return;
        if (dt > MaxDt) dt = MaxDt;

        if (!Config.Running) return;

        lock (_gate)
        {
            foreach (TaskItem t in _tasks)
            {
                if (t.Status == TaskStatus.Done) continue;

                float rotBefore = t.Rot;
                Stage before = StageOf(t);
                t.Rot = (float)Math.Min(t.Rot + RotDelta(t, Config, dt), RotMax);
                Stage after = StageOf(t);

                if (after != before)
                {
                    switch (after)
                    {
                        case Stage.Larva when before == Stage.Egg:
                            Events.Add(new EngineEvent(EngineEventType.Hatched, t.Id, t.Title));
                            break;
                        case Stage.Pupa:
                            Events.Add(new EngineEvent(EngineEventType.Pupated, t.Id, t.Title));
                            break;
                        case Stage.Fly:
                            Events.Add(new EngineEvent(EngineEventType.BecameFly, t.Id, t.Title));
                            break;
                    }
                }

                // 预警线：只在「越过」的那一帧发一次，避免每帧刷屏。
                if (rotBefore < ApproachT && t.Rot >= ApproachT && t.Rot < FlyT)
                {
                    Events.Add(new EngineEvent(EngineEventType.ApproachingFly, t.Id, t.Title));
                }
            }

            BreedStep(dt, now);
        }
    }

    /// <summary>
    /// maxFlies 的唯一用途：给繁殖封顶（规格第 7 节）。
    ///
    /// ⚠️ 这里**只**影响繁殖。绝对不允许反过来去冻结腐烂 ——
    /// 即使苍蝇已到上限，正在腐烂的任务仍然必须正常变成苍蝇，
    /// 只是不再产生新的卵。给 rot 设「随苍蝇数量下降的天花板」会让任务
    /// 永久卡在「即将变苍蝇」，直接推翻产品最核心的承诺。
    /// </summary>
    internal static bool BreedingAtCapacity(MaggotConfig config, int flyCount, int taskCount)
        => flyCount >= config.MaxFlies || taskCount >= config.MaxTasks;

    /// <summary>规格第 7 节：繁殖（失控的来源）。调用方需已持有锁。</summary>
    private void BreedStep(double dt, long now)
    {
        if (!Config.BreedEnabled) { _breedAccum = 0; return; }

        List<TaskItem> flies = new();
        foreach (TaskItem t in _tasks)
        {
            if (StageOf(t) == Stage.Fly) flies.Add(t);
        }

        // 顺序与规格第 7 节一致：
        //   一只苍蝇不繁殖 → 苍蝇数量触顶 → 任务总数触顶
        if (flies.Count < 2) { _breedAccum = 0; return; }
        if (flies.Count >= Config.MaxFlies) { _breedAccum = 0; return; }
        if (_tasks.Count >= Config.MaxTasks) { _breedAccum = 0; return; }

        _breedAccum += dt * Config.RotSpeed;

        double interval = Config.BreedSeconds / (1.0 + (flies.Count - 2) * 0.06);
        if (interval <= 0) interval = 1.0;
        if (_breedAccum < interval) return;

        _breedAccum = 0;

        TaskItem a = flies[_rng.Next(flies.Count)];
        TaskItem b = a;
        while (ReferenceEquals(b, a) && flies.Count > 1)
        {
            b = flies[_rng.Next(flies.Count)];
        }

        int gen = Math.Max(a.Generation, b.Generation) + 1;
        string sourceTitle = Truncate(a.Title, 10);
        string title = $"蝇卵 #{gen} · {sourceTitle}";

        TaskItem? child = AddTaskCore(title, string.Empty, gen, a.Id, now);
        if (child is not null)
        {
            Events.Add(EngineEvent.Bred(a.Id, b.Id, child.Id));
        }
    }

    // ------------------------------------------------------------------
    // 5. 「我去做了」
    // ------------------------------------------------------------------

    /// <summary>
    /// 软性冷却：factor 随时间从 CARE_MIN_FACTOR 线性涨到 1，
    /// 冷却期内点击依然有反馈，但连续刷收益递减。
    /// </summary>
    public static float CareFactor(long now, long lastCaredAt)
    {
        double sinceSec = (now - lastCaredAt) / 1000.0;
        if (sinceSec < 0) sinceSec = 0;
        double progress = Math.Min(sinceSec / CareCooldownSec, 1.0);
        return (float)(CareMinFactor + (1.0 - CareMinFactor) * progress);
    }

    /// <summary>计算 care 的效果，不落地。返回 (下降量, 进度增量)。</summary>
    public static (float Relief, int ProgressDelta) PreviewCare(TaskItem task, long now)
    {
        float factor = CareFactor(now, task.LastCaredAt);
        float relief = CareRelief * factor;
        int delta = CareProgressMin +
                    (int)Math.Round(factor * (CareProgressMax - CareProgressMin),
                                    MidpointRounding.AwayFromZero);
        return (relief, delta);
    }

    /// <summary>「我去做了」。返回实际倒扣的腐烂度。</summary>
    public float Care(string taskId, long now)
    {
        lock (_gate)
        {
            TaskItem? t = Find(taskId);
            if (t is null || t.Status == TaskStatus.Done) return 0f;

            (float relief, int delta) = PreviewCare(t, now);

            float before = t.Rot;
            t.Rot = Math.Max(0f, t.Rot - relief);
            t.LastCaredAt = now;
            t.Status = TaskStatus.Doing;
            t.Progress = Math.Min(100, t.Progress + delta);

            return before - t.Rot;
        }
    }

    // ------------------------------------------------------------------
    // 6. 拍死
    // ------------------------------------------------------------------

    /// <summary>拍死任务。返回 false 表示任务不存在或已经拍过。</summary>
    public bool Complete(string taskId, long now, out TaskItem? squashed)
    {
        lock (_gate)
        {
            TaskItem? t = Find(taskId);
            if (t is null || t.Status == TaskStatus.Done)
            {
                squashed = null;
                return false;
            }

            t.Status = TaskStatus.Done;
            t.Progress = 100;
            t.CompletedAt = now;
            _squashedCount++;
            squashed = t;
            Events.Add(new EngineEvent(EngineEventType.Squashed, t.Id, t.Title));
            TrimSquashed();
            return true;
        }
    }

    /// <summary>「战果」最多保留 50 条，超出清理最旧的。调用方需已持有锁。</summary>
    private void TrimSquashed()
    {
        List<TaskItem> done = new();
        foreach (TaskItem t in _tasks)
        {
            if (t.Status == TaskStatus.Done) done.Add(t);
        }

        if (done.Count <= SquashKeep) return;

        done.Sort((x, y) => (x.CompletedAt ?? x.CreatedAt).CompareTo(y.CompletedAt ?? y.CreatedAt));
        int excess = done.Count - SquashKeep;
        for (int i = 0; i < excess; i++)
        {
            _tasks.Remove(done[i]);
            _index.Remove(done[i].Id);
        }
    }

    // ------------------------------------------------------------------
    // 任务增删
    // ------------------------------------------------------------------

    /// <summary>新建任务（用户亲手创建，generation = 0）。</summary>
    public TaskItem? AddTask(string title, string note = "", long? now = null)
    {
        lock (_gate)
        {
            long ts = now ?? NowMs();
            TaskItem? t = AddTaskCore(title, note, 0, null, ts);
            return t;
        }
    }

    /// <summary>调用方需已持有锁。</summary>
    private TaskItem? AddTaskCore(string title, string note, int generation, string? parentId, long now,
        string? dailyTemplateId = null, string? dailyKey = null,
        string? weeklyTemplateId = null, string? weeklyKey = null)
    {
        if (_tasks.Count >= Config.MaxTasks) return null;

        TaskItem t = new()
        {
            Id = Guid.NewGuid().ToString("D"),
            Title = string.IsNullOrWhiteSpace(title) ? "未命名任务" : title.Trim(),
            Note = note ?? string.Empty,
            Status = TaskStatus.Todo,
            Rot = 0f,
            Progress = 0,
            CreatedAt = now,
            LastCaredAt = now,
            CompletedAt = null,
            Generation = generation,
            ParentId = parentId,
            Gene = GeneFor(generation),
            DailyTemplateId = dailyTemplateId,
            DailyKey = dailyKey,
            WeeklyTemplateId = weeklyTemplateId,
            WeeklyKey = weeklyKey,
        };

        _tasks.Add(t);
        _index[t.Id] = t;
        return t;
    }

    /// <summary>删除单个任务。返回是否删除成功。</summary>
    public bool RemoveTask(string taskId)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(taskId, out TaskItem? t)) return false;
            _tasks.Remove(t);
            _index.Remove(taskId);
            return true;
        }
    }

    /// <summary>
    /// 重置：所有任务 rot = 0、progress 保留、lastCaredAt = now；苍蝇全部退回卵。不删任务。
    /// </summary>
    public void ResetAll(long? now = null)
    {
        lock (_gate)
        {
            long ts = now ?? NowMs();
            foreach (TaskItem t in _tasks)
            {
                t.Rot = 0f;
                t.LastCaredAt = ts;
            }
            _breedAccum = 0;
        }
    }

    /// <summary>清空：删除全部任务，计数归零。</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _tasks.Clear();
            _index.Clear();
            _breedAccum = 0;
            _squashedCount = 0;
        }
    }

    /// <summary>用外部数据整体替换（持久化加载用）。不触发事件。</summary>
    public void ReplaceAll(IEnumerable<TaskItem> tasks, long? squashedCount = null)
    {
        lock (_gate)
        {
            _tasks.Clear();
            _index.Clear();
            foreach (TaskItem t in tasks)
            {
                if (_tasks.Count >= Config.MaxTasks) break;
                if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("D");
                if (t.Gene <= 0) t.Gene = GeneFor(t.Generation);
                if (t.LastCaredAt <= 0) t.LastCaredAt = t.CreatedAt > 0 ? t.CreatedAt : NowMs();
                if (t.CreatedAt <= 0) t.CreatedAt = t.LastCaredAt;
                if (_index.ContainsKey(t.Id)) continue;
                _tasks.Add(t);
                _index[t.Id] = t;
            }
            _breedAccum = 0;
            _squashedCount = squashedCount ?? 0;
            TrimSquashed();
        }
    }

    // ------------------------------------------------------------------
    // 12. 每日任务（模板 + 幂等生成）
    // ------------------------------------------------------------------

    /// <summary>每日模板的只读快照（按 sortOrder 稳定排序）。</summary>
    public IReadOnlyList<DailyTemplate> DailyTemplates()
    {
        lock (_gate)
        {
            DailyTemplate[] copy = new DailyTemplate[_dailyTemplates.Count];
            for (int i = 0; i < _dailyTemplates.Count; i++) copy[i] = _dailyTemplates[i].Clone();
            return copy;
        }
    }

    /// <summary>整体替换每日模板（持久化加载用）。不触发任何生成。</summary>
    public void ReplaceDailyTemplates(IEnumerable<DailyTemplate> templates)
    {
        lock (_gate)
        {
            _dailyTemplates.Clear();
            int order = 0;
            foreach (DailyTemplate t in templates)
            {
                if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("D");
                if (t.SortOrder == 0) t.SortOrder = order;
                order++;
                _dailyTemplates.Add(t);
            }
        }
    }

    /// <summary>
    /// 规格 §12.2：确保每个启用的模板在 <paramref name="todayKey"/> 这天有一个实例。
    ///
    /// 幂等 —— 同一天无论调用多少次，每个模板最多只会有一个实例；
    /// 跨天（todayKey 变了）时自然再生成一个，旧实例保持原样继续腐烂。
    /// </summary>
    /// <returns>本次实际新建的实例数。</returns>
    public int EnsureDailyTasks(string todayKey, long now)
    {
        if (string.IsNullOrEmpty(todayKey)) return 0;

        lock (_gate)
        {
            if (!Config.DailyEnabled) return 0;

            // 按 sortOrder 排序后遍历，保证生成顺序稳定（规格 §12.2）。
            List<DailyTemplate> ordered = new(_dailyTemplates);
            ordered.Sort(static (a, b) =>
            {
                int byOrder = a.SortOrder.CompareTo(b.SortOrder);
                return byOrder != 0 ? byOrder : string.CompareOrdinal(a.CreatedAt.ToString(), b.CreatedAt.ToString());
            });

            int created = 0;
            foreach (DailyTemplate t in ordered)
            {
                if (!t.Enabled) continue;
                if (HasInstanceCore(t.Id, todayKey)) continue;
                if (SpawnDailyInstanceCore(t, todayKey, now) is not null) created++;
            }
            return created;
        }
    }

    /// <summary>今天的实例（按当前本地日期）。</summary>
    public int EnsureDailyTasksToday(long now) => EnsureDailyTasks(DailyKeys.Today(), now);

    /// <summary>某个模板在指定日期键上是否已经有实例。</summary>
    public bool HasDailyInstance(string templateId, string dailyKey)
    {
        lock (_gate)
        {
            return HasInstanceCore(templateId, dailyKey);
        }
    }

    /// <summary>某个模板在某天生成的实例（没有则 null）。</summary>
    public TaskItem? FindDailyInstance(string templateId, string dailyKey)
    {
        lock (_gate)
        {
            foreach (TaskItem t in _tasks)
            {
                if (t.DailyTemplateId == templateId && t.DailyKey == dailyKey) return t;
            }
            return null;
        }
    }

    /// <summary>某个模板生成过的全部实例（含已拍死的）。</summary>
    public IReadOnlyList<TaskItem> DailyInstancesOf(string templateId)
    {
        lock (_gate)
        {
            List<TaskItem> result = new();
            foreach (TaskItem t in _tasks)
            {
                if (t.DailyTemplateId == templateId) result.Add(t.Clone());
            }
            return result;
        }
    }

    /// <summary>
    /// 规格 §12.4「新增模板」：立刻检查一次今天是否已有实例，没有就**马上生成** ——
    /// 用户点完就能看见虫子出现。
    /// </summary>
    /// <returns>新模板；标题为空时返回 null。</returns>
    public DailyTemplate? AddDailyTemplate(string title, string note = "", long? now = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        lock (_gate)
        {
            long ts = now ?? NowMs();
            DailyTemplate t = new()
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = title.Trim(),
                Note = note ?? string.Empty,
                Enabled = true,
                CreatedAt = ts,
                SortOrder = _dailyTemplates.Count,
            };
            _dailyTemplates.Add(t);

            // 立刻生成今天的实例（受 dailyEnabled / maxTasks 约束，失败也不影响模板本身）。
            if (Config.DailyEnabled) SpawnDailyInstanceCore(t, DailyKeys.FromUnixMs(ts), ts);
            return t.Clone();
        }
    }

    /// <summary>
    /// 规格 §12.4「停用模板」：不再生成新实例，**已有实例不受影响**。
    /// </summary>
    public bool SetDailyTemplateEnabled(string templateId, bool enabled)
    {
        lock (_gate)
        {
            DailyTemplate? t = FindTemplateCore(templateId);
            if (t is null) return false;
            t.Enabled = enabled;
            return true;
        }
    }

    /// <summary>
    /// 规格 §12.4「删除模板」：移除模板。已有实例**保留**，
    /// 只是 dailyTemplateId 变成孤儿 —— 它们退化成普通任务继续烂。
    /// </summary>
    public bool RemoveDailyTemplate(string templateId)
    {
        lock (_gate)
        {
            DailyTemplate? t = FindTemplateCore(templateId);
            if (t is null) return false;
            _dailyTemplates.Remove(t);
            return true;
        }
    }

    /// <summary>清空全部模板（不删实例）。</summary>
    public void ClearDailyTemplates()
    {
        lock (_gate)
        {
            _dailyTemplates.Clear();
        }
    }

    private DailyTemplate? FindTemplateCore(string templateId)
    {
        foreach (DailyTemplate t in _dailyTemplates)
        {
            if (string.Equals(t.Id, templateId, StringComparison.Ordinal)) return t;
        }
        return null;
    }

    private bool HasInstanceCore(string templateId, string dailyKey)
    {
        foreach (TaskItem t in _tasks)
        {
            if (string.Equals(t.DailyTemplateId, templateId, StringComparison.Ordinal)
                && string.Equals(t.DailyKey, dailyKey, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 生成一个每日实例。调用方需已持有锁。
    ///
    /// 规格 §12.3：实例与普通任务没有任何生命周期上的区别 ——
    /// generation = 0、gene = 1.0、rot = 0、status = TODO，只是标题带日期、带了来源标记。
    /// </summary>
    private TaskItem? SpawnDailyInstanceCore(DailyTemplate template, string dailyKey, long now)
    {
        TaskItem? t = AddTaskCore(
            DailyKeys.InstanceTitle(template.Title, dailyKey),
            template.Note,
            generation: 0,
            parentId: null,
            now: now,
            dailyTemplateId: template.Id,
            dailyKey: dailyKey);

        if (t is null) return null;

        t.Gene = GeneFor(0); // 1.0 —— 每日实例不吃繁殖加成
        Events.Add(EngineEvent.DailySpawned(t.Id, t.Title, template.Id, dailyKey));
        return t;
    }

    /// <summary>
    /// 某模板「今天」的实例状态文案：未生成 / 卵 / 蛆 / 蛹 / 苍蝇 / 已完成（§12.5 的界面要求）。
    /// </summary>
    public static string DailyInstanceStatusText(TaskItem? instance)
    {
        if (instance is null) return "未生成";
        if (instance.Status == TaskStatus.Done) return "已完成";
        return StageOf(instance).Label();
    }

    // ------------------------------------------------------------------
    // 15. 每周任务（模板 + 幂等生成）
    //
    // 与 §12 每日任务**逐行同构** —— 唯一的区别是多一个「星期几」，
    // 以及周键换成 ISO-8601 的 "YYYY-Www"（周基准年）。
    // ------------------------------------------------------------------

    /// <summary>每周模板的只读快照（按 sortOrder 稳定排序）。</summary>
    public IReadOnlyList<WeeklyTemplate> WeeklyTemplates()
    {
        lock (_gate)
        {
            WeeklyTemplate[] copy = new WeeklyTemplate[_weeklyTemplates.Count];
            for (int i = 0; i < _weeklyTemplates.Count; i++) copy[i] = _weeklyTemplates[i].Clone();
            return copy;
        }
    }

    /// <summary>整体替换每周模板（持久化加载用）。不触发任何生成。</summary>
    public void ReplaceWeeklyTemplates(IEnumerable<WeeklyTemplate> templates)
    {
        lock (_gate)
        {
            _weeklyTemplates.Clear();
            int order = 0;
            foreach (WeeklyTemplate t in templates)
            {
                if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("D");
                if (t.SortOrder == 0) t.SortOrder = order;
                t.Weekday = Math.Clamp(t.Weekday, 1, 7);   // json 被手改过也不至于崩
                order++;
                _weeklyTemplates.Add(t);
            }
        }
    }

    /// <summary>
    /// 规格 §15.4：确保每个启用的模板在本周有一个实例。
    ///
    /// 周键与星期几都从 <paramref name="now"/> 的**设备本地日期**推导 —— 绝不用 UTC。
    /// </summary>
    /// <returns>本次实际新建的实例数。</returns>
    public int EnsureWeeklyTasks(long now)
    {
        DateTime local = DateTimeOffset.FromUnixTimeMilliseconds(now).LocalDateTime;
        return EnsureWeeklyTasks(WeeklyKeys.FromLocal(local), WeeklyKeys.IsoWeekday(local), now);
    }

    /// <summary>
    /// 规格 §15.4 的核心：确保每个启用的模板在 <paramref name="weekKey"/> 这一周有一个实例。
    ///
    /// 幂等 —— 同一周内无论调用多少次，每个模板最多只会有一个实例；
    /// 跨周（weekKey 变了）时自然再生成一个，旧实例保持原样继续腐烂。
    ///
    /// <paramref name="isoWeekday"/> 是「1=周一 … 7=周日」的 ISO 编号；
    /// <c>isoWeekday &lt; tpl.weekday</c> 时跳过（还没到该生成的那天）——
    /// 反之（迟到）照样补上，不跳过这一周。
    /// </summary>
    /// <returns>本次实际新建的实例数。</returns>
    public int EnsureWeeklyTasks(string weekKey, int isoWeekday, long now)
    {
        if (string.IsNullOrEmpty(weekKey)) return 0;

        lock (_gate)
        {
            if (!Config.WeeklyEnabled) return 0;

            // 按 sortOrder 排序后遍历，保证生成顺序稳定（规格 §15.4）。
            List<WeeklyTemplate> ordered = new(_weeklyTemplates);
            ordered.Sort(static (a, b) =>
            {
                int byOrder = a.SortOrder.CompareTo(b.SortOrder);
                return byOrder != 0 ? byOrder : a.CreatedAt.CompareTo(b.CreatedAt);
            });

            int created = 0;
            foreach (WeeklyTemplate t in ordered)
            {
                if (!t.Enabled) continue;
                if (isoWeekday < t.Weekday) continue;      // 还没到该生成的那天
                if (HasWeeklyInstanceCore(t.Id, weekKey)) continue;
                if (SpawnWeeklyInstanceCore(t, weekKey, now) is not null) created++;
            }
            return created;
        }
    }

    /// <summary>某个模板在指定周键上是否已经有实例。</summary>
    public bool HasWeeklyInstance(string templateId, string weekKey)
    {
        lock (_gate)
        {
            return HasWeeklyInstanceCore(templateId, weekKey);
        }
    }

    /// <summary>某个模板在某一周生成的实例（没有则 null）。</summary>
    public TaskItem? FindWeeklyInstance(string templateId, string weekKey)
    {
        lock (_gate)
        {
            foreach (TaskItem t in _tasks)
            {
                if (t.WeeklyTemplateId == templateId && t.WeeklyKey == weekKey) return t;
            }
            return null;
        }
    }

    /// <summary>某个模板生成过的全部实例（含已拍死的）。</summary>
    public IReadOnlyList<TaskItem> WeeklyInstancesOf(string templateId)
    {
        lock (_gate)
        {
            List<TaskItem> result = new();
            foreach (TaskItem t in _tasks)
            {
                if (t.WeeklyTemplateId == templateId) result.Add(t.Clone());
            }
            return result;
        }
    }

    /// <summary>
    /// 规格 §15.5 → §12.4「新增模板」：今天已经到达该星期就**立刻**生成本周实例 ——
    /// 用户点完就能看见虫子出现；还没到那天就不生成（§15.6-2）。
    /// </summary>
    /// <returns>新模板；标题为空时返回 null。</returns>
    public WeeklyTemplate? AddWeeklyTemplate(string title, int weekday, string note = "", long? now = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        lock (_gate)
        {
            long ts = now ?? NowMs();
            DateTime local = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime;
            string weekKey = WeeklyKeys.FromLocal(local);
            int isoWeekday = WeeklyKeys.IsoWeekday(local);

            WeeklyTemplate t = new()
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = title.Trim(),
                Note = note ?? string.Empty,
                Enabled = true,
                CreatedAt = ts,
                SortOrder = _weeklyTemplates.Count,
                Weekday = Math.Clamp(weekday, 1, 7),
            };
            _weeklyTemplates.Add(t);

            if (Config.WeeklyEnabled && isoWeekday >= t.Weekday)
            {
                SpawnWeeklyInstanceCore(t, weekKey, ts);
            }
            return t.Clone();
        }
    }

    /// <summary>
    /// 规格 §15.5 → §12.4「停用模板」：不再生成新实例，**已有实例不受影响**。
    /// </summary>
    public bool SetWeeklyTemplateEnabled(string templateId, bool enabled)
    {
        lock (_gate)
        {
            WeeklyTemplate? t = FindWeeklyTemplateCore(templateId);
            if (t is null) return false;
            t.Enabled = enabled;
            return true;
        }
    }

    /// <summary>
    /// 规格 §15.5 → §12.4「删除模板」：移除模板。已有实例**保留**，
    /// 只是 weeklyTemplateId 变成孤儿 —— 它们退化成普通任务继续烂。
    /// </summary>
    public bool RemoveWeeklyTemplate(string templateId)
    {
        lock (_gate)
        {
            WeeklyTemplate? t = FindWeeklyTemplateCore(templateId);
            if (t is null) return false;
            _weeklyTemplates.Remove(t);
            return true;
        }
    }

    /// <summary>清空全部每周模板（不删实例）。</summary>
    public void ClearWeeklyTemplates()
    {
        lock (_gate)
        {
            _weeklyTemplates.Clear();
        }
    }

    private WeeklyTemplate? FindWeeklyTemplateCore(string templateId)
    {
        foreach (WeeklyTemplate t in _weeklyTemplates)
        {
            if (string.Equals(t.Id, templateId, StringComparison.Ordinal)) return t;
        }
        return null;
    }

    private bool HasWeeklyInstanceCore(string templateId, string weekKey)
    {
        foreach (TaskItem t in _tasks)
        {
            if (string.Equals(t.WeeklyTemplateId, templateId, StringComparison.Ordinal)
                && string.Equals(t.WeeklyKey, weekKey, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 生成一个每周实例。调用方需已持有锁。
    ///
    /// 规格 §15.5（= §12.3）：实例与普通任务没有任何生命周期上的区别 ——
    /// generation = 0、gene = 1.0、rot = 0、status = TODO，只是标题带周号、带了来源标记。
    /// </summary>
    private TaskItem? SpawnWeeklyInstanceCore(WeeklyTemplate template, string weekKey, long now)
    {
        TaskItem? t = AddTaskCore(
            WeeklyKeys.InstanceTitle(template.Title, weekKey),
            template.Note,
            generation: 0,
            parentId: null,
            now: now,
            weeklyTemplateId: template.Id,
            weeklyKey: weekKey);

        if (t is null) return null;

        t.Gene = GeneFor(0); // 1.0 —— 每周实例不吃繁殖加成
        Events.Add(EngineEvent.WeeklySpawned(t.Id, t.Title, template.Id, weekKey));
        return t;
    }

    /// <summary>
    /// 某模板「本周」的实例状态文案：未生成 / 卵 / 蛆 / 蛹 / 苍蝇 / 已完成。
    /// 与 <see cref="DailyInstanceStatusText"/> 刻意保持同一份文案。
    /// </summary>
    public static string WeeklyInstanceStatusText(TaskItem? instance)
    {
        if (instance is null) return "未生成";
        if (instance.Status == TaskStatus.Done) return "已完成";
        return StageOf(instance).Label();
    }

    // ------------------------------------------------------------------
    // 8. 离线补算
    // ------------------------------------------------------------------

    /// <summary>
    /// 应用重新回到前台（或服务重启）时补算腐烂。
    /// 用 capped 作为 dt 一次性推进，不发繁殖事件。
    /// </summary>
    public double ApplyOfflineDecay(long lastSeenAt, long now)
    {
        double elapsedSec = Math.Max(0.0, (now - lastSeenAt) / 1000.0);
        double capped = Math.Min(elapsedSec, OfflineCapMin * 60.0);
        LastOfflineCatchUpSeconds = 0;

        if (capped <= 1.0) return 0.0;

        lock (_gate)
        {
            foreach (TaskItem t in _tasks)
            {
                if (t.Status == TaskStatus.Done) continue;

                float rotBefore = t.Rot;
                Stage before = StageOf(t);
                t.Rot = (float)Math.Min(t.Rot + RotDelta(t, Config, capped), RotMax);
                Stage after = StageOf(t);

                if (after != before)
                {
                    switch (after)
                    {
                        case Stage.Larva when before == Stage.Egg:
                            Events.Add(new EngineEvent(EngineEventType.Hatched, t.Id, t.Title));
                            break;
                        case Stage.Pupa:
                            Events.Add(new EngineEvent(EngineEventType.Pupated, t.Id, t.Title));
                            break;
                        case Stage.Fly:
                            Events.Add(new EngineEvent(EngineEventType.BecameFly, t.Id, t.Title));
                            break;
                    }
                }
            }
            // 不同步交配：离线期间不繁殖。
            _breedAccum = 0;
        }

        LastOfflineCatchUpSeconds = capped;
        return capped;
    }

    /// <summary>启动时检查「已经是苍蝇」的任务，用于汇总通知（不发阶段事件）。</summary>
    public List<TaskItem> TasksAlreadyFlies()
    {
        lock (_gate)
        {
            List<TaskItem> result = new();
            foreach (TaskItem t in _tasks)
            {
                if (StageOf(t) == Stage.Fly) result.Add(t);
            }
            return result;
        }
    }

    /// <summary>清空事件队列并返回内容。</summary>
    public List<EngineEvent> DrainEvents()
    {
        lock (_gate)
        {
            if (Events.Count == 0) return new List<EngineEvent>();
            List<EngineEvent> copy = new(Events);
            Events.Clear();
            return copy;
        }
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>O(1) 按 id 查任务（返回引擎内部实例）。</summary>
    public TaskItem? FindById(string id)
    {
        lock (_gate)
        {
            return _index.TryGetValue(id, out TaskItem? t) ? t : null;
        }
    }

    private TaskItem? Find(string id) => _index.TryGetValue(id, out TaskItem? t) ? t : null;

    /// <summary>按显示字符截断（中文算一个字）。</summary>
    public static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>阶段占比，用于控制面板的腐烂压力条。</summary>
    public (int Egg, int Larva, int Pupa, int Fly, int Dead) StageCounts()
    {
        lock (_gate)
        {
            int egg = 0, larva = 0, pupa = 0, fly = 0, dead = 0;
            foreach (TaskItem t in _tasks)
            {
                switch (StageOf(t))
                {
                    case Stage.Egg: egg++; break;
                    case Stage.Larva: larva++; break;
                    case Stage.Pupa: pupa++; break;
                    case Stage.Fly: fly++; break;
                    default: dead++; break;
                }
            }
            return (egg, larva, pupa, fly, dead);
        }
    }

    /// <summary>把剩余秒数格式化为「剩余 12 分钟」这类中文文案。</summary>
    public string RemainingText(TaskItem task) => RemainingText(task.Rot);

    /// <summary>按当前配置计算「还剩多久变苍蝇」。</summary>
    public string RemainingText(float rot)
    {
        if (rot >= FlyT) return "已成蝇";

        TaskItem probe = new() { Rot = rot, Gene = 1f, Status = TaskStatus.Todo };
        double rate = RotRatePerSecond(probe, Config);
        if (rate <= 0) return "剩余 —";

        double sec = (FlyT - rot) / rate;
        if (sec <= 0) return "已成蝇";
        if (sec < 60) return $"剩余 {Math.Max(1, (int)Math.Round(sec))} 秒";
        if (sec < 3600) return $"剩余 {Math.Max(1, (int)Math.Round(sec / 60.0))} 分钟";
        double hours = sec / 3600.0;
        if (hours < 24) return $"剩余 {hours:0.#} 小时";
        return $"剩余 {hours / 24.0:0.#} 天";
    }
}
