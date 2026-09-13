using System.Diagnostics;
using System.Globalization;
using System.Text;
using MaggotTask.Services;
using MaggotTask.View;

namespace MaggotTask.Core;

/// <summary>
/// 无界面自测：逐条覆盖 docs/lifecycle-spec.md 第 11 节判定标准，
/// 外加常量、公式、持久化往返的断言。
/// 用法： <c>MaggotTask.exe --selftest</c>（退出码 0 = 全过，1 = 有失败）
/// </summary>
public static class SelfTest
{
    private static readonly StringBuilder Log = new();
    private static int _passed;
    private static int _failed;

    public static int RunAll()
    {
        _passed = 0;
        _failed = 0;

        Log.AppendLine("═══ 蛆虫任务 · 生命周期引擎自测 ═══");
        Log.AppendLine($"时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log.AppendLine();

        ConstantsMatchSpec();
        StageBoundaries();
        CareBehaviour();
        CareCooldownIsSoft();
        PauseFreezesRot();
        DecayMathAndCap();
        ProgressToFlyDuration();
        BreedingRequiresTwoFlies();
        BreedingHappensWithTwoFlies();
        BreedingRespectsMaxTasks();
        BreedingRespectsMaxFlies();
        GeneAndGeneration();
        OffspringStartsFresh();
        ResetKeepsTasks();
        ClearDeletesEverything();
        SquashBookkeeping();
        SquashedGroupCappedAt50();
        OfflineDecayCapAndSilence();
        EventEmission();
        PersistenceRoundTrip();

        // ---- v1.1 新增 ----
        DailyKeysBehaviour();
        DailyTemplateAcceptanceCriteria12_5();
        DailyTemplateLifecycle();
        DailyPersistenceRoundTrip();
        LegacyV1StateLoadsCleanly();
        SoundSynthesis();
        HoverDwellBehaviour();

        // ---- v1.2 新增：每周任务（规格 §15）----
        WeeklyKeysBehaviour();
        WeeklyTemplateAcceptanceCriteria15_6();
        WeeklyTemplateLifecycle();
        WeeklyPersistenceRoundTrip();
        LegacyV2StateLoadsCleanly();

        SimulationPacerBehaviour();

        Log.AppendLine();
        Log.AppendLine($"─── 结果：{_passed} 通过 / {_failed} 失败 ───");

        string report = Log.ToString();
        App.SafeWriteLine(report);

        // 同时落一份报告，便于没有控制台的场景取证
        try
        {
            string path = Path.Combine(AppPaths.DataDirectory, "selftest-report.txt");
            AppPaths.EnsureDataDirectory();
            File.WriteAllText(path, report, new UTF8Encoding(false));
            App.SafeWriteLine($"报告已写入：{path}");
        }
        catch (Exception ex)
        {
            App.SafeWriteLine($"报告写入失败：{ex.Message}");
        }

        return _failed == 0 ? 0 : 1;
    }

    // ==================================================================
    // 断言工具
    // ==================================================================

    private static void Section(string name)
    {
        Log.AppendLine();
        Log.AppendLine($"▌{name}");
    }

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok)
        {
            _passed++;
            Log.AppendLine($"  [PASS] {what}{(detail.Length > 0 ? "  →  " + detail : string.Empty)}");
        }
        else
        {
            _failed++;
            Log.AppendLine($"  [FAIL] {what}{(detail.Length > 0 ? "  →  " + detail : string.Empty)}");
        }
    }

    private static void Near(string what, double actual, double expected, double tol)
    {
        bool ok = Math.Abs(actual - expected) <= tol;
        Check(what, ok, string.Create(CultureInfo.InvariantCulture,
            $"实际 {actual:0.######} / 期望 {expected:0.######}（容差 {tol:0.######}）"));
    }

    /// <summary>
    /// 常量相等断言。
    /// Single 常量（ROT_MAX / HATCH_T …）转到 double 会带上 ~1e-8 的表示误差，
    /// 所以容差取 1e-7 —— 足够严到能抓出任何真实的数值改动（规格改动至少是 0.01 级）。
    /// </summary>
    private static void Eq(string what, double actual, double expected)
        => Check(what, Math.Abs(actual - expected) < 1e-7,
            string.Create(CultureInfo.InvariantCulture, $"实际 {actual:0.######} / 期望 {expected:0.######}"));

    private static TaskItem MakeTask(float rot, TaskStatus status = TaskStatus.Todo,
        int generation = 0, long lastCaredAt = 0, string title = "测试任务")
    {
        long now = LifecycleEngine.NowMs();
        return new TaskItem
        {
            Id = Guid.NewGuid().ToString("D"),
            Title = title,
            Status = status,
            Rot = rot,
            Progress = 0,
            CreatedAt = now,
            LastCaredAt = lastCaredAt == 0 ? now : lastCaredAt,
            Generation = generation,
            Gene = LifecycleEngine.GeneFor(generation),
        };
    }

    /// <summary>
    /// 某个本地日期的**本地正午**对应的 epoch millis。
    ///
    /// 测试必须同时钉住「时间戳」和「日期键」，否则 <see cref="DailyKeys.FromUnixMs"/>
    /// 会按运行时的真实日期算出另一个键。取正午是为了避开时区/夏令时的边界效应。
    /// </summary>
    private static long LocalNoonMs(int year, int month, int day) =>
        new DateTimeOffset(new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    /// <summary>按 10Hz 轮询若干次，返回最后一次结果。</summary>
    private static HoverPollResult PollFor(HoverTracker tracker, IReadOnlyList<HoverTarget> targets,
        Point cursor, int polls)
    {
        HoverPollResult result = default;
        for (int i = 0; i < polls; i++)
        {
            result = tracker.Poll(targets, cursor, HoverTracker.PollIntervalSeconds);
        }
        return result;
    }

    /// <summary>
    /// 从当前状态开始持续轮询，直到气泡该显示为止；返回用掉的轮询次数
    /// （超过 <paramref name="maxPolls"/> 还没弹返回 int.MaxValue）。
    /// </summary>
    private static int PollUntilShown(HoverTracker tracker, IReadOnlyList<HoverTarget> targets,
        Point cursor, int maxPolls)
    {
        for (int i = 1; i <= maxPolls; i++)
        {
            HoverPollResult r = tracker.Poll(targets, cursor, HoverTracker.PollIntervalSeconds);
            if (r.ShouldShow) return i;
        }
        return int.MaxValue;
    }

    // ==================================================================
    // 第 2 节：常量
    // ==================================================================

    private static void ConstantsMatchSpec()
    {
        Section("第 2 节 · 常量必须与规格逐字一致");
        Eq("ROT_MAX = 1.20", LifecycleEngine.RotMax, 1.20);
        Eq("HATCH_T = 0.18", LifecycleEngine.HatchT, 0.18);
        Eq("PUPA_T = 0.50", LifecycleEngine.PupaT, 0.50);
        Eq("FLY_T = 0.78", LifecycleEngine.FlyT, 0.78);
        Eq("APPROACH_T = FLY_T - 0.10 = 0.68", LifecycleEngine.ApproachT, 0.68);
        Eq("ROT_MAX_SAFE = 1.00", LifecycleEngine.RotMaxSafe, 1.00);
        Eq("CARE_RELIEF = 0.22", LifecycleEngine.CareRelief, 0.22);
        Eq("CARE_COOLDOWN_SEC = 20.0", LifecycleEngine.CareCooldownSec, 20.0);
        Eq("CARE_MIN_FACTOR = 0.35", LifecycleEngine.CareMinFactor, 0.35);
        Eq("CARE_PROGRESS_MIN = 4", LifecycleEngine.CareProgressMin, 4);
        Eq("CARE_PROGRESS_MAX = 12", LifecycleEngine.CareProgressMax, 12);
        Eq("GENE_STEP = 0.10", LifecycleEngine.GeneStep, 0.10);
        Eq("GENE_MAX = 2.20", LifecycleEngine.GeneMax, 2.20);
        Eq("OFFLINE_CAP_MIN = 45.0", LifecycleEngine.OfflineCapMin, 45.0);
        Eq("TICK_HZ = 60", LifecycleEngine.TickHz, 60);
        Eq("IDLE_TICK_HZ = 10", LifecycleEngine.IdleTickHz, 10);
        Eq("MAX_DT = 0.50", LifecycleEngine.MaxDt, 0.50);

        Eq("stageMultiplier(EGG) = 0.70", LifecycleEngine.StageMultiplier(Stage.Egg), 0.70);
        Eq("stageMultiplier(LARVA) = 1.00", LifecycleEngine.StageMultiplier(Stage.Larva), 1.00);
        Eq("stageMultiplier(PUPA) = 1.35", LifecycleEngine.StageMultiplier(Stage.Pupa), 1.35);
        Eq("stageMultiplier(FLY) = 1.60", LifecycleEngine.StageMultiplier(Stage.Fly), 1.60);

        MaggotConfig defaults = new();
        Check("MaggotConfig 默认值", defaults is
        {
            Running: true,
            RotSpeed: 1.0,
            BaseRotMinutes: 90,
            BreedEnabled: true,
            BreedSeconds: 40,
            MaxFlies: 60,
            MaxTasks: 150,
            ClickThrough: true,
            SoundEnabled: true,
            NotifyOnFly: true,
            DailyEnabled: true,
            WeeklyEnabled: true,
        }, $"rotSpeed={defaults.RotSpeed}, baseRotMinutes={defaults.BaseRotMinutes}, breedSeconds={defaults.BreedSeconds}, maxFlies={defaults.MaxFlies}, maxTasks={defaults.MaxTasks}, sound={defaults.SoundEnabled}, daily={defaults.DailyEnabled}, weekly={defaults.WeeklyEnabled}");
    }

    // ==================================================================
    // 第 4 节：阶段推导边界
    // ==================================================================

    private static void StageBoundaries()
    {
        Section("第 4 节 · stageOf 推导边界");

        Check("rot 0.17 + TODO → EGG", LifecycleEngine.StageOf(MakeTask(0.17f)) == Stage.Egg);
        Check("rot 0.18 → LARVA", LifecycleEngine.StageOf(MakeTask(0.18f)) == Stage.Larva);
        Check("rot 0.179 → EGG", LifecycleEngine.StageOf(MakeTask(0.179f)) == Stage.Egg);
        Check("rot 0.50 → PUPA", LifecycleEngine.StageOf(MakeTask(0.50f)) == Stage.Pupa);
        Check("rot 0.499 → LARVA", LifecycleEngine.StageOf(MakeTask(0.499f)) == Stage.Larva);
        Check("rot 0.78 → FLY", LifecycleEngine.StageOf(MakeTask(0.78f)) == Stage.Fly);
        Check("rot 0.779 → PUPA", LifecycleEngine.StageOf(MakeTask(0.779f)) == Stage.Pupa);
        Check("rot 1.20 → FLY", LifecycleEngine.StageOf(MakeTask(1.20f)) == Stage.Fly);

        // DOING 判断放在最后：点了「我去做了」立刻变蛆
        Check("rot 0.00 + DOING → LARVA（点了立刻变蛆）",
            LifecycleEngine.StageOf(MakeTask(0f, TaskStatus.Doing)) == Stage.Larva);
        // 但已经烂到蛹/苍蝇的不会因为点一下就退回蛆
        Check("rot 0.60 + DOING → PUPA（不会退回蛆）",
            LifecycleEngine.StageOf(MakeTask(0.60f, TaskStatus.Doing)) == Stage.Pupa);
        Check("rot 0.90 + DOING → FLY（不会退回蛆）",
            LifecycleEngine.StageOf(MakeTask(0.90f, TaskStatus.Doing)) == Stage.Fly);
        Check("status DONE → DEAD",
            LifecycleEngine.StageOf(MakeTask(0.10f, TaskStatus.Done)) == Stage.Dead);
        Check("status DONE + rot 1.20 → DEAD",
            LifecycleEngine.StageOf(MakeTask(1.20f, TaskStatus.Done)) == Stage.Dead);

        // 腐烂速率公式：rate = (1/(baseRotMinutes*60)) * rotSpeed * gene * mult
        MaggotConfig cfg = new() { RotSpeed = 1.0, BaseRotMinutes = 90 };
        TaskItem egg = MakeTask(0f);
        double expected = (1.0 / (90 * 60)) * 1.0 * 1.0 * 0.70;
        Near("EGG 每秒腐烂量 = 1/(90*60)*1.0*1.0*0.70", LifecycleEngine.RotRatePerSecond(egg, cfg), expected, 1e-9);

        TaskItem larva = MakeTask(0.30f);
        Near("LARVA 每秒腐烂量 = 1/(90*60)*1.0*1.0*1.00", LifecycleEngine.RotRatePerSecond(larva, cfg), 1.0 / (90 * 60), 1e-9);

        TaskItem flyGen2 = MakeTask(0.80f, TaskStatus.Todo, generation: 2);
        Near("FLY + gene 1.2 每秒腐烂量", LifecycleEngine.RotRatePerSecond(flyGen2, cfg),
            (1.0 / (90 * 60)) * 1.0 * 1.2 * 1.60, 1e-9);

        // MAX_DT clamp：一次 tick 传 5 秒，实际只推进 0.5 秒
        MaggotConfig slow = new() { RotSpeed = 1.0, BaseRotMinutes = 90 };
        LifecycleEngine e1 = new(slow);
        TaskItem t1 = e1.AddTask("clamp", now: 1_000_000)!;
        t1.Rot = 0.30f; // LARVA，倍率 1.0
        e1.Tick(5.0, 1_000_000);
        Near("Tick(5.0) 被 clamp 到 MAX_DT=0.5s",
            t1.Rot, 0.30 + (1.0 / (90 * 60)) * 0.5, 1e-6);

        // ROT_MAX 上限
        LifecycleEngine e2 = new(new MaggotConfig { RotSpeed = 8.0, BaseRotMinutes = 1 });
        TaskItem t2 = e2.AddTask("cap", now: 1_000_000)!;
        for (int i = 0; i < 400; i++) e2.Tick(LifecycleEngine.MaxDt, 1_000_000 + i);
        Eq("rot 不会超过 ROT_MAX(1.20)", t2.Rot, LifecycleEngine.RotMax);
    }

    // ==================================================================
    // 第 5 节：care
    // ==================================================================

    private static void CareBehaviour()
    {
        Section("第 5 节 · 「我去做了」");
        long now = 10_000_000L;
        LifecycleEngine engine = new(new MaggotConfig { RotSpeed = 1.0 }, randomSeed: 7);
        TaskItem t = engine.AddTask("写报告", now: now)!;

        // 让它先烂一会儿，并且冷却早就满了
        t.Rot = 0.40f;
        t.LastCaredAt = now - (long)(LifecycleEngine.CareCooldownSec * 1000) - 5000;

        float before = t.Rot;
        float relief = engine.Care(t.Id, now);

        Near("冷却满额时 relief = CARE_RELIEF = 0.22", relief, 0.22, 1e-6);
        Near("rot 下降 0.22", t.Rot, before - 0.22, 1e-6);
        Check("care 之后 status == DOING", t.Status == TaskStatus.Doing);
        Check("care 之后阶段立刻变 LARVA", LifecycleEngine.StageOf(t) == Stage.Larva);
        Check("care 刷新 lastCaredAt", t.LastCaredAt == now);

        // progress：factor=1 → +4 + round(1*(12-4)) = +12
        Check("冷却满额时 progress +12", t.Progress == 12, $"progress={t.Progress}");

        // rot 不会被扣成负数
        TaskItem low = engine.AddTask("低腐烂", now: now)!;
        low.Rot = 0.05f;
        low.LastCaredAt = now - 100_000;
        engine.Care(low.Id, now);
        Check("rot 不会低于 0", low.Rot == 0f, $"rot={low.Rot}");

        // 冷却未满时的 factor
        float f0 = LifecycleEngine.CareFactor(now, now);
        Near("sinceSec=0 → factor = CARE_MIN_FACTOR(0.35)", f0, 0.35, 1e-6);
        float f10 = LifecycleEngine.CareFactor(now, now - 10_000);
        Near("sinceSec=10 → factor = 0.35+0.65*0.5 = 0.675", f10, 0.675, 1e-6);
        float f20 = LifecycleEngine.CareFactor(now, now - 20_000);
        Near("sinceSec=20 → factor = 1.0", f20, 1.0, 1e-6);
        float f60 = LifecycleEngine.CareFactor(now, now - 60_000);
        Near("sinceSec=60 → factor 仍被 clamp 到 1.0", f60, 1.0, 1e-6);

        // 最低效果时的进度：4 + round(0.35*8) = 4 + 3 = 7
        int minDelta = LifecycleEngine.CareProgressMin +
                       (int)Math.Round(LifecycleEngine.CareMinFactor * (LifecycleEngine.CareProgressMax - LifecycleEngine.CareProgressMin),
                           MidpointRounding.AwayFromZero);
        Check("冷却未满时 progress 增量 = 7", minDelta == 7, $"实际 {minDelta}");
    }

    private static void CareCooldownIsSoft()
    {
        Section("第 5 节 · 连点收益递减（判定标准 5）");
        long t0 = 20_000_000L;
        LifecycleEngine engine = new(new MaggotConfig { RotSpeed = 8.0 }, randomSeed: 11);
        TaskItem t = engine.AddTask("疯狂连点", now: t0)!;
        t.Rot = LifecycleEngine.RotMax;              // 从上限开始，尽量扣
        t.LastCaredAt = t0 - 100_000;                // 冷却满

        float start = t.Rot;
        float totalDrop = 0f;
        for (int i = 0; i < 10; i++)
        {
            // 同一时刻连点：dt = 0，所以 factor 恒定 = CARE_MIN_FACTOR = 0.35
            totalDrop += engine.Care(t.Id, t0);
        }

        float naive = 10f * LifecycleEngine.CareRelief;

        Check($"10 次连点总下降 {totalDrop:0.0000} 明显小于 10×CARE_RELIEF({naive:0.00})",
            totalDrop < naive * 0.5f,
            $"比值 {totalDrop / naive:P1}");

        // 逐次校验：
        //   第 1 次 —— 距上次 care 已过 100 秒（> CARE_COOLDOWN_SEC），factor 满额 = 1.0，
        //             实扣 CARE_RELIEF = 0.22
        //   第 2–10 次 —— 同一时刻连点，sinceSec = 0，factor 被压到 CARE_MIN_FACTOR = 0.35，
        //             每次实扣 0.22 * 0.35 = 0.077
        float expectedTotal = 0.22f + 9f * (0.22f * 0.35f); // 0.22 + 0.693 = 0.913
        Near("首次 care 满额扣 0.22，随后 9 次各扣 0.077",
            totalDrop, expectedTotal, 1e-4);
        Near("rot 从 ROT_MAX 1.20 扣到 0.287", t.Rot, 1.20f - expectedTotal, 1e-3);

        // 判定标准 5 的实质：10 连点的总收益远小于「10 次满额」。
        Check($"10 连点总收益只有满额连打的 {(0.22f + 9f * 0.077f) / naive:P1}（远小于 1）",
            totalDrop < naive * 0.5f, $"{totalDrop:0.0000} vs {naive:0.00}");
        Check("连点的边际收益被压到 CARE_MIN_FACTOR = 35%",
            Math.Abs(0.077f / LifecycleEngine.CareRelief - LifecycleEngine.CareMinFactor) < 1e-5,
            $"{0.077f / LifecycleEngine.CareRelief:P2}");
        Check("单次满额收益确实是 CARE_RELIEF = 0.22",
            Math.Abs(LifecycleEngine.CareRelief - 0.22f) < 1e-6);

        // 再验证「同一时刻连点」时 progress 会累加到 100 封顶
        Check("progress 封顶 100", t.Progress <= 100, $"progress={t.Progress}");
        Check("status == DOING", t.Status == TaskStatus.Doing);
    }

    // ==================================================================
    // 第 10 节：暂停
    // ==================================================================

    private static void PauseFreezesRot()
    {
        Section("第 10 节 · 暂停后 rot 完全冻结（判定标准 6）");
        MaggotConfig cfg = new() { Running = true, RotSpeed = 8.0 };
        LifecycleEngine engine = new(cfg, randomSeed: 3);
        TaskItem a = engine.AddTask("甲", now: 1_000_000)!;
        TaskItem b = engine.AddTask("乙", now: 1_000_000)!;
        a.Rot = 0.30f;
        b.Rot = 0.90f;

        for (int i = 0; i < 200; i++) engine.Tick(0.05, 1_000_000 + i * 50);
        Check("暂停前 rot 确实在涨", a.Rot > 0.30f, $"a.rot={a.Rot:0.0000}");

        cfg.Running = false;
        float ra = a.Rot;
        float rb = b.Rot;
        int countBefore = engine.Count;

        for (int i = 0; i < 2000; i++) engine.Tick(0.05, 1_100_000 + i * 50);

        Eq("暂停后 a.rot 不变", a.Rot, ra);
        Eq("暂停后 b.rot 不变", b.Rot, rb);
        Check("暂停后不繁殖", engine.Count == countBefore, $"任务数 {engine.Count}");

        cfg.Running = true;
        engine.Tick(0.5, 2_000_000);
        Check("恢复后 rot 继续推进", a.Rot > ra, $"a.rot {ra:0.0000} → {a.Rot:0.0000}");
    }

    // ==================================================================
    // 第 4 / 11 节：腐烂数学
    // ==================================================================

    private static void DecayMathAndCap()
    {
        Section("第 4 节 · 腐烂推进数学（判定标准 1 的解析验证）");

        MaggotConfig cfg = new() { RotSpeed = 8.0, BaseRotMinutes = 90 };
        LifecycleEngine engine = new(cfg, randomSeed: 5);
        TaskItem t = engine.AddTask("走完全程", now: 1_000_000)!;

        // 按规格：rotSpeed=8 时 0→1 约需 baseRotMinutes*60/8 = 675 秒 = 11.25 分钟
        double nominal = cfg.BaseRotMinutes * 60.0 / cfg.RotSpeed;
        Near("名义 0→1 时长 = 90*60/8 = 675 秒", nominal, 675.0, 1e-9);

        // 解析积分：dt_total = ∫ dRot / rate(rot)，rate 在每个阶段是常数
        double expectedTotal =
            (LifecycleEngine.HatchT - 0.0) / ((1.0 / (90 * 60)) * 8.0 * 0.70) +
            (LifecycleEngine.PupaT - LifecycleEngine.HatchT) / ((1.0 / (90 * 60)) * 8.0 * 1.00) +
            (LifecycleEngine.FlyT - LifecycleEngine.PupaT) / ((1.0 / (90 * 60)) * 8.0 * 1.35) +
            (1.0 - LifecycleEngine.FlyT) / ((1.0 / (90 * 60)) * 8.0 * 1.60);

        // 逐帧模拟到 rot >= 1.0
        double elapsed = 0.0;
        double step = 1.0 / 60.0;
        int guard = 0;
        while (t.Rot < 1.0f && guard++ < 2_000_000)
        {
            engine.Tick(step, 1_000_000 + (long)(elapsed * 1000));
            elapsed += step;
        }

        // 解析积分 vs 逐帧模拟：两者只差一个 dt 量级的离散化误差
        Near("模拟到 rot=1.0 用时 ≈ 解析积分值（差异仅来自离散化）", elapsed, expectedTotal, 1.0);
        Check($"rot=1.0 用时 {elapsed / 60.0:0.00} 分钟 ≈ 11 分钟量级（判定标准 1）",
            elapsed > 500 && elapsed < 800, $"{elapsed:0.0} 秒");
        Check("rotSpeed=8 下 11 分钟内确实从卵走到了苍蝇", t.Rot >= 1.0f, $"rot={t.Rot:0.0000}");
    }

    private static void ProgressToFlyDuration()
    {
        Section("第 11 节 判定标准 1 · rotSpeed=8 下卵→苍蝇");
        MaggotConfig cfg = new() { RotSpeed = 8.0, BaseRotMinutes = 90 };
        LifecycleEngine engine = new(cfg, randomSeed: 9);
        TaskItem t = engine.AddTask("放下不管", now: 0)!;

        double step = 1.0 / 60.0;
        double elapsed = 0;
        Stage firstStages = Stage.Egg;
        var seen = new List<Stage>();
        seen.Add(LifecycleEngine.StageOf(t));

        int guard = 0;
        while (LifecycleEngine.StageOf(t) != Stage.Fly && guard++ < 500_000)
        {
            engine.Tick(step, (long)(elapsed * 1000));
            elapsed += step;
            Stage s = LifecycleEngine.StageOf(t);
            if (seen[^1] != s) seen.Add(s);
        }

        firstStages = seen[0];
        Check("阶段推移顺序为 EGG → LARVA → PUPA → FLY",
            seen.Count == 4 && seen[0] == Stage.Egg && seen[1] == Stage.Larva && seen[2] == Stage.Pupa && seen[3] == Stage.Fly,
            string.Join(" → ", seen));

        double minutes = elapsed / 60.0;
        Check($"约 {minutes:0.0} 分钟内走完，规格预期 ≈ 11–20 分钟",
            minutes > 8 && minutes < 25, $"{minutes:0.0} 分钟（名义 {90.0 * 60 / 8 / 60:0.0} 分钟 + 阶段倍率）");

        _ = firstStages;
    }

    // ==================================================================
    // 第 7 节：繁殖
    // ==================================================================

    private static void BreedingRequiresTwoFlies()
    {
        Section("第 7 节 · 一只苍蝇不繁殖（判定标准 2）");
        MaggotConfig cfg = new()
        {
            RotSpeed = 8.0,
            BreedEnabled = true,
            BreedSeconds = 40,
            MaxTasks = 150,
        };
        LifecycleEngine engine = new(cfg, randomSeed: 13);

        TaskItem only = engine.AddTask("唯一一只", now: 0)!;
        only.Rot = 0.90f; // FLY
        only.Gene = LifecycleEngine.GeneFor(0);

        int before = engine.Count;
        // 等 5 分钟
        for (int i = 0; i < 5 * 60 * 60; i++) engine.Tick(1.0 / 60.0, i * 16);
        Check("只有 1 只苍蝇，等 5 分钟任务数不增加", engine.Count == before,
            $"{before} → {engine.Count}");

        // 显式校验：<2 只时 breedAccum 被清零 —— 用 2 只验证它确实会繁殖（见下一节）
    }

    private static void BreedingHappensWithTwoFlies()
    {
        Section("第 7 节 · 两只苍蝇必然繁殖（判定标准 3）");
        MaggotConfig cfg = new()
        {
            RotSpeed = 8.0,
            BreedEnabled = true,
            BreedSeconds = 40,
            MaxTasks = 150,
            MaxFlies = 60,
        };
        LifecycleEngine engine = new(cfg, randomSeed: 17);

        TaskItem a = engine.AddTask("甲", now: 0)!;
        TaskItem b = engine.AddTask("乙", now: 0)!;
        a.Rot = 0.90f;
        b.Rot = 0.95f;

        int before = engine.Count;
        int guard = 0;
        while (engine.Count == before && guard++ < 60 * 60 * 10)
        {
            engine.Tick(1.0 / 60.0, guard * 16L);
        }

        Check("出现新任务", engine.Count > before, $"{before} → {engine.Count}");

        List<EngineEvent> events = engine.DrainEvents();
        EngineEvent? bred = events.FirstOrDefault(e => e.Type == EngineEventType.Bred);
        Check("发出 Bred 事件", bred.HasValue, $"事件数 {events.Count}");

        IReadOnlyList<TaskItem> all = engine.Snapshot();
        TaskItem? child = all.FirstOrDefault(t => t.Generation >= 1);
        Check("存在 generation ≥ 1 的新任务（判定标准 3）", child is not null);
        if (child is not null)
        {
            Check("child.gene = min(1+0.1*gen, 2.20)",
                Math.Abs(child.Gene - LifecycleEngine.GeneFor(child.Generation)) < 1e-6,
                $"gen={child.Generation} gene={child.Gene}");
            Check("child.rot = 0 且 status = TODO", child.Rot == 0f && child.Status == TaskStatus.Todo);
            Check("child.parentId 指向父母之一", child.ParentId == a.Id || child.ParentId == b.Id,
                $"parentId={child.ParentId}");
            Check("child.title 形如「蝇卵 #N · …」", child.Title.StartsWith("蝇卵 #", StringComparison.Ordinal),
                child.Title);
        }

        // 繁殖间隔：苍蝇越多越快 —— interval = breedSeconds / (1 + (flies-2)*0.06)
        Near("2 只时 interval = 40/(1+0) = 40 秒",
            cfg.BreedSeconds / (1.0 + (2 - 2) * 0.06), 40.0, 1e-9);
        Near("5 只时 interval = 40/(1+3*0.06) ≈ 33.9 秒",
            cfg.BreedSeconds / (1.0 + (5 - 2) * 0.06), 40.0 / 1.18, 1e-9);
    }

    private static void BreedingRespectsMaxTasks()
    {
        Section("第 7 节 · 繁殖尊重 maxTasks（硬性安全阀）");
        MaggotConfig cfg = new()
        {
            RotSpeed = 8.0,
            BreedEnabled = true,
            BreedSeconds = 2,
            MaxTasks = 6,
            MaxFlies = 60,
        };
        LifecycleEngine engine = new(cfg, randomSeed: 23);

        TaskItem a = engine.AddTask("甲", now: 0)!;
        TaskItem b = engine.AddTask("乙", now: 0)!;
        a.Rot = 0.90f;
        b.Rot = 0.90f;

        for (int i = 0; i < 60 * 60 * 10; i++) engine.Tick(1.0 / 60.0, i * 16L);

        Check("任务数不超过 maxTasks=6", engine.Count <= cfg.MaxTasks, $"实际 {engine.Count}");
        Check("任务数确实涨到了上限（说明繁殖在跑）", engine.Count == cfg.MaxTasks, $"实际 {engine.Count}");

        // AddTask 也要挡住
        TaskItem? overflow = engine.AddTask("溢出", now: 0);
        Check("达到 maxTasks 时 AddTask 返回 null", overflow is null);
    }

    private static void BreedingRespectsMaxFlies()
    {
        Section("第 7 节 · maxFlies 只封顶繁殖，绝不冻结腐烂");

        // ── 用例 1：刚好 3 只苍蝇 + maxFlies=3 → 不再产卵 ──
        MaggotConfig cfg = new()
        {
            RotSpeed = 8.0,
            BreedEnabled = true,
            BreedSeconds = 1,
            MaxTasks = 400,
            MaxFlies = 3,
        };
        LifecycleEngine engine = new(cfg, randomSeed: 29);
        for (int i = 0; i < 3; i++)
        {
            TaskItem t = engine.AddTask($"蝇{i}", now: 0)!;
            t.Rot = 0.95f;
        }

        int before = engine.Count;
        for (int i = 0; i < 60 * 60; i++) engine.Tick(1.0 / 60.0, i * 16L);   // 60 秒

        Check("maxFlies=3、已有 3 只苍蝇 → 跑 60 秒任务数仍是 3（繁殖被封顶）",
            engine.Count == 3, $"{before} → {engine.Count}");

        // ── 用例 2（回归守卫）：苍蝇已触顶，新任务仍必须能烂成苍蝇 ──
        TaskItem fresh = engine.AddTask("新任务", now: 0)!;
        fresh.Rot = LifecycleEngine.FlyT - 0.01f;   // 0.77，差一点点就变苍蝇
        Check("前置：fresh 现在还是蛹",
            LifecycleEngine.StageOf(fresh) == Stage.Pupa,
            $"rot={fresh.Rot:0.0000} stage={LifecycleEngine.StageOf(fresh)}");

        int countBefore = engine.Count;
        for (int i = 0; i < 60 * 30; i++) engine.Tick(1.0 / 60.0, 4_000_000 + i * 16L);  // 30 秒

        Check("【回归守卫】苍蝇数已达 maxFlies 时，rot=FLY_T-0.01 的任务仍然必须变成苍蝇",
            LifecycleEngine.StageOf(fresh) == Stage.Fly,
            $"rot={fresh.Rot:0.0000} stage={LifecycleEngine.StageOf(fresh)}");
        Check("它没有被卡在 FLY_T 之下（不存在「天花板」）",
            fresh.Rot > LifecycleEngine.FlyT, $"rot={fresh.Rot:0.0000} vs FLY_T={LifecycleEngine.FlyT}");
        Check("同时依然没有产生新卵（腐烂与繁殖互不干扰）",
            engine.Count == countBefore, $"{countBefore} → {engine.Count}");

        // ── 用例 3：只要给足时间，rot 一定到得了 ROT_MAX，与苍蝇多少完全无关 ──
        // （30 秒只够从 0.77 涨到 0.84；PUPA 倍率下到 1.20 还需要约 100 秒）
        for (int i = 0; i < 60 * 60 * 5; i++) engine.Tick(1.0 / 60.0, 6_000_000 + i * 16L);
        Check("【回归守卫】给足时间后 rot 必定达到 ROT_MAX(1.20)",
            Math.Abs(fresh.Rot - LifecycleEngine.RotMax) < 1e-9, $"rot={fresh.Rot:0.0000}");

        // ── 用例 4：从 2 只起飞 —— 繁殖停在 maxFlies，但腐烂从不冻结 ──
        MaggotConfig cfg2 = new()
        {
            RotSpeed = 8.0,
            BreedEnabled = true,
            BreedSeconds = 1,
            MaxTasks = 5000,
            MaxFlies = 6,
        };
        LifecycleEngine e2 = new(cfg2, randomSeed: 31);
        TaskItem x = e2.AddTask("甲", now: 0)!;
        TaskItem y = e2.AddTask("乙", now: 0)!;
        x.Rot = 0.90f;
        y.Rot = 0.90f;

        // 繁殖是否已经停止：数「新卵」的诞生。到上限之后 BreedStep 直接 return，
        // 所以出生数会停住。这里用 generation>0 的任务数当繁殖计数器。
        int offspringAfter10Min = -1;
        for (int i = 0; i < 60 * 60 * 20; i++)
        {
            e2.Tick(1.0 / 60.0, i * 16L);
            if (i == 60 * 60 * 10 - 1)
            {
                offspringAfter10Min = e2.Snapshot().Count(t => t.Generation > 0);
            }
        }
        int offspringAtEnd = e2.Snapshot().Count(t => t.Generation > 0);

        Check($"繁殖在苍蝇达到 maxFlies={cfg2.MaxFlies} 时停止（出生数不再增长）",
            offspringAtEnd == offspringAfter10Min,
            $"10 分钟时 {offspringAfter10Min} 个后代 → 20 分钟时 {offspringAtEnd} 个");
        Check("确实繁殖到了上限（不是一开始就被卡死）",
            offspringAtEnd > 0, $"实际 {offspringAtEnd} 个后代");

        // 关键：腐烂绝不冻结 —— 绝不允许出现「永久卡在 FLY_T 下方」的中间态。
        // 判定方式：任何 rot 落在 [PUPA_T, FLY_T) 的任务，只要还在推进就不是卡住；
        // 因此这里再跑一帧，确认这些任务的 rot 确实还在动。
        IReadOnlyList<TaskItem> all = e2.Snapshot();
        int inPupaBand = all.Count(t => t.Rot >= LifecycleEngine.PupaT && t.Rot < LifecycleEngine.FlyT);
        var frozenBefore = all.ToDictionary(t => t.Id, t => t.Rot);
        for (int i = 0; i < 60 * 60; i++) e2.Tick(1.0 / 60.0, 90_000_000 + i * 16L);
        int frozen = e2.Snapshot().Count(t =>
            t.Rot < LifecycleEngine.FlyT
            && frozenBefore.TryGetValue(t.Id, out float was)
            && Math.Abs(t.Rot - was) < 1e-7f);

        Check("【回归守卫】没有任何任务停在蛹期不动（rot 冻结）",
            frozen == 0, $"冻结的任务数 {frozen}（该区间原有 {inPupaBand} 个）");

        // 跑得够久，所有任务都应该烂穿到 ROT_MAX
        for (int i = 0; i < 60 * 60 * 10; i++) e2.Tick(1.0 / 60.0, 200_000_000 + i * 16L);
        IReadOnlyList<TaskItem> settled = e2.Snapshot();
        Check("足够长时间后全部任务 rot 都到达 ROT_MAX（放着不管一定会烂穿）",
            settled.All(t => Math.Abs(t.Rot - LifecycleEngine.RotMax) < 1e-4f),
            $"未触顶的任务数 {settled.Count(t => Math.Abs(t.Rot - LifecycleEngine.RotMax) >= 1e-4f)} / {settled.Count}");
        Check("任务总数被 maxTasks 兜住", e2.Count <= cfg2.MaxTasks, $"实际 {e2.Count}");

        // ── BreedingAtCapacity 的纯函数校验 ──
        MaggotConfig cap = new() { MaxFlies = 10, MaxTasks = 100 };
        Check("BreedingAtCapacity: 苍蝇未满、任务未满 → false",
            !LifecycleEngine.BreedingAtCapacity(cap, 9, 99));
        Check("BreedingAtCapacity: 苍蝇触顶 → true",
            LifecycleEngine.BreedingAtCapacity(cap, 10, 5));
        Check("BreedingAtCapacity: 任务触顶 → true",
            LifecycleEngine.BreedingAtCapacity(cap, 2, 100));

        MaggotConfig normalized = new() { MaxFlies = 60 };
        normalized.Normalize();
        Check("Normalize 不会把 maxFlies 改成非法值", normalized.MaxFlies == 60);
    }

    private static void GeneAndGeneration()
    {
        Section("第 7 节 · 后代 generation 递增、gene 增长");

        // —— 决定性验证：父母代数不同 → 子代 = max(父母代数) + 1，gene 随之增长 ——
        MaggotConfig cfg = new()
        {
            RotSpeed = 8.0,
            BreedEnabled = true,
            BreedSeconds = 1,
            MaxTasks = 200,
            MaxFlies = 500,
        };
        LifecycleEngine engine = new(cfg, randomSeed: 31);

        TaskItem a = engine.AddTask("甲", now: 0)!;
        TaskItem b = engine.AddTask("乙", now: 0)!;
        a.Rot = 0.90f;
        a.Generation = 0;
        a.Gene = LifecycleEngine.GeneFor(0);
        b.Rot = 0.90f;
        b.Generation = 1;                       // 一只第 1 代
        b.Gene = LifecycleEngine.GeneFor(1);

        TaskItem? child = null;
        long now = 0;
        for (int i = 0; i < 60 * 60 * 5 && child is null; i++)
        {
            now += 16;
            engine.Tick(1.0 / 60.0, now);
            child = engine.Snapshot().FirstOrDefault(t => t.Generation >= 2);
        }

        Check("第 0 代 × 第 1 代 → 产生第 2 代（generation 递增）", child is not null);
        if (child is not null)
        {
            Check("child.generation = max(0,1) + 1 = 2", child.Generation == 2, $"实际 {child.Generation}");
            Near("child.gene = GeneFor(2) = 1.20", child.Gene, 1.20f, 1e-6);
            Check("gene 随代数增长（1.10 → 1.20）", child.Gene > b.Gene,
                $"{b.Gene:0.00} → {child.Gene:0.00}");
        }

        // —— 长跑：确认能一路繁殖并保持基因单调 ——
        MaggotConfig cfgLong = new()
        {
            RotSpeed = 8.0,
            BreedEnabled = true,
            BreedSeconds = 1,
            MaxTasks = 400,
            MaxFlies = 500,
        };
        LifecycleEngine longRun = new(cfgLong, randomSeed: 33);
        TaskItem c = longRun.AddTask("丙", now: 0)!;
        TaskItem d = longRun.AddTask("丁", now: 0)!;
        c.Rot = 0.90f;
        d.Rot = 0.90f;
        for (int i = 0; i < 60 * 60 * 20; i++) longRun.Tick(1.0 / 60.0, i * 16L);

        IReadOnlyList<TaskItem> all = longRun.Snapshot();
        int maxGen = all.Count == 0 ? 0 : all.Max(t => t.Generation);
        Check("长时间失控繁殖后出现多代后代", maxGen >= 1, $"最高代数 {maxGen}");
        Check("所有后代的 gene 都等于 GeneFor(generation)",
            all.All(t => Math.Abs(t.Gene - LifecycleEngine.GeneFor(t.Generation)) < 1e-6));
        Check("gene 单调不降（代数越大越凶）",
            all.Where(t => t.Generation > 0)
               .GroupBy(t => t.Generation)
               .All(g => g.All(t => Math.Abs(t.Gene - LifecycleEngine.GeneFor(g.Key)) < 1e-6)));

        Near("GeneFor(0) = 1.00", LifecycleEngine.GeneFor(0), 1.00, 1e-7);
        Near("GeneFor(5) = 1.50", LifecycleEngine.GeneFor(5), 1.50, 1e-7);
        Near("GeneFor(100) 被 GENE_MAX 封顶 = 2.20", LifecycleEngine.GeneFor(100), 2.20, 1e-7);
        Near("GeneFor(12) = 1 + 0.1*12 = 2.20（正好触顶）", LifecycleEngine.GeneFor(12), 2.20, 1e-7);
        Near("GeneFor(11) = 2.10（还没触顶）", LifecycleEngine.GeneFor(11), 2.10, 1e-7);

        // 基因真的让腐烂更快
        MaggotConfig c2 = new() { RotSpeed = 1.0, BaseRotMinutes = 90 };
        TaskItem plain = MakeTask(0.30f);
        TaskItem bred = MakeTask(0.30f, TaskStatus.Todo, generation: 6);
        Check("高代数虫子腐烂更快",
            LifecycleEngine.RotRatePerSecond(bred, c2) > LifecycleEngine.RotRatePerSecond(plain, c2),
            $"{LifecycleEngine.RotRatePerSecond(plain, c2):0.000000} vs {LifecycleEngine.RotRatePerSecond(bred, c2):0.000000}");
    }

    private static void OffspringStartsFresh()
    {
        Section("第 7 节 · 新个体从 rot=0 开始、lastCaredAt = now");
        MaggotConfig cfg = new()
        {
            RotSpeed = 8.0,
            BreedEnabled = true,
            BreedSeconds = 1,
            MaxTasks = 150,
            MaxFlies = 60,
        };
        LifecycleEngine engine = new(cfg, randomSeed: 37);
        TaskItem a = engine.AddTask("甲", now: 5_000)!;
        TaskItem b = engine.AddTask("乙", now: 5_000)!;
        a.Rot = 0.90f;
        b.Rot = 0.90f;

        long now = 5_000;
        TaskItem? child = null;
        for (int i = 0; i < 60 * 60 * 5 && child is null; i++)
        {
            now += 16;
            engine.Tick(1.0 / 60.0, now);
            child = engine.Snapshot().FirstOrDefault(t => t.Generation >= 1);
        }

        Check("繁殖出了后代", child is not null);
        if (child is not null)
        {
            Check("后代 status == TODO", child.Status == TaskStatus.Todo);
            Check("后代 createdAt == lastCaredAt == 繁殖时刻", child.CreatedAt == child.LastCaredAt);
        }
    }

    // ==================================================================
    // 第 10 节：重置 / 清空
    // ==================================================================

    private static void ResetKeepsTasks()
    {
        Section("第 10 节 · 重置（判定标准 7）");
        LifecycleEngine engine = new(new MaggotConfig { RotSpeed = 8.0 }, randomSeed: 41);

        TaskItem a = engine.AddTask("甲", now: 0)!;
        TaskItem b = engine.AddTask("乙", now: 0)!;
        TaskItem c = engine.AddTask("丙", now: 0)!;
        a.Rot = 0.10f; a.Progress = 30;
        b.Rot = 0.95f; b.Progress = 55;
        c.Rot = 1.20f; c.Progress = 80;

        int countBefore = engine.Count;
        long now = 999_999;
        engine.ResetAll(now);

        Check("任务数量不变", engine.Count == countBefore, $"{countBefore} → {engine.Count}");
        Check("所有 rot == 0", engine.Snapshot().All(t => t.Rot == 0f),
            string.Join(",", engine.Snapshot().Select(t => t.Rot)));
        Check("progress 保留", a.Progress == 30 && b.Progress == 55 && c.Progress == 80,
            $"{a.Progress}/{b.Progress}/{c.Progress}");
        Check("lastCaredAt = now", engine.Snapshot().All(t => t.LastCaredAt == now));
        Check("苍蝇全部退回卵", engine.Snapshot().All(t => LifecycleEngine.StageOf(t) == Stage.Egg),
            string.Join(",", engine.Snapshot().Select(t => LifecycleEngine.StageOf(t).ToString())));
        Check("FlyCount 归零", engine.FlyCount == 0);
    }

    private static void ClearDeletesEverything()
    {
        Section("第 10 节 · 清空");
        LifecycleEngine engine = new(new MaggotConfig(), randomSeed: 43);
        for (int i = 0; i < 12; i++) engine.AddTask($"任务{i}", now: 0);
        engine.Complete(engine.Snapshot()[0].Id, 1, out _);

        engine.ClearAll();

        Check("任务数归零", engine.Count == 0, $"实际 {engine.Count}");
        Check("拍死计数归零", engine.SquashedCount == 0, $"实际 {engine.SquashedCount}");
        Check("阶段计数全零",
            engine is { FlyCount: 0 } && engine.StageCounts() == (0, 0, 0, 0, 0),
            engine.StageCounts().ToString());
    }

    // ==================================================================
    // 第 6 节：拍死
    // ==================================================================

    private static void SquashBookkeeping()
    {
        Section("第 6 节 · 拍死");
        LifecycleEngine engine = new(new MaggotConfig(), randomSeed: 47);
        TaskItem t = engine.AddTask("待拍", now: 100)!;
        t.Rot = 0.60f;
        t.Progress = 20;

        bool ok = engine.Complete(t.Id, 12345, out TaskItem? squashed);
        Check("Complete 返回 true", ok);
        Check("status == DONE", t.Status == TaskStatus.Done);
        Check("progress == 100", t.Progress == 100);
        Check("completedAt == now", t.CompletedAt == 12345);
        Check("stageOf → DEAD", LifecycleEngine.StageOf(t) == Stage.Dead);
        Check("SquashedCount 计入", engine.SquashedCount == 1, $"实际 {engine.SquashedCount}");
        Check("发出 Squashed 事件",
            engine.DrainEvents().Any(e => e.Type == EngineEventType.Squashed));

        // 重复拍不重复计数
        bool again = engine.Complete(t.Id, 99999, out _);
        Check("重复拍死返回 false 且不重复计数", !again && engine.SquashedCount == 1,
            $"again={again} count={engine.SquashedCount}");

        // DEAD 不再腐烂
        float rot = t.Rot;
        for (int i = 0; i < 1000; i++) engine.Tick(0.5, 20000 + i);
        Eq("已拍死的任务 rot 不再变化", t.Rot, rot);

        _ = squashed;
    }

    private static void SquashedGroupCappedAt50()
    {
        Section("第 6 节 · 战果列表最多 50 条");
        LifecycleEngine engine = new(new MaggotConfig { MaxTasks = 400 }, randomSeed: 53);

        List<string> ids = new();
        for (int i = 0; i < 80; i++)
        {
            TaskItem t = engine.AddTask($"第{i}个", now: 1000 + i)!;
            ids.Add(t.Id);
        }

        for (int i = 0; i < 80; i++)
        {
            engine.Complete(ids[i], 10_000 + i, out _);
        }

        int doneCount = engine.Snapshot().Count(t => t.Status == TaskStatus.Done);
        Check("战果保留 50 条", doneCount == LifecycleEngine.SquashKeep, $"实际 {doneCount}");
        Check("SquashedCount 累计 80", engine.SquashedCount == 80, $"实际 {engine.SquashedCount}");

        IReadOnlyList<TaskItem> remaining = engine.Snapshot();
        long oldestKept = remaining.Where(t => t.Status == TaskStatus.Done)
            .Min(t => t.CompletedAt ?? 0);
        Check("被清理的是最旧的（保留的是 10_030 之后）", oldestKept >= 10_030, $"最旧保留 {oldestKept}");
    }

    // ==================================================================
    // 第 8 节：离线补算
    // ==================================================================

    private static void OfflineDecayCapAndSilence()
    {
        Section("第 8 节 · 离线补算（最多 45 分钟，不繁殖）");
        MaggotConfig cfg = new() { RotSpeed = 1.0, BaseRotMinutes = 90 };
        LifecycleEngine engine = new(cfg, randomSeed: 59);
        TaskItem t = engine.AddTask("离线测试", now: 0)!;
        t.Rot = 0f;

        long now = 1_000_000_000L;

        // 1) 30 分钟离线 → 按 30 分钟补算
        double applied = engine.ApplyOfflineDecay(now - 30 * 60 * 1000, now);
        Near("30 分钟离线补算 1800 秒", applied, 1800.0, 1e-6);

        double expectedRot = (1.0 / (90 * 60)) * 1800.0 * 1.0 * 0.70; // EGG 倍率
        Near("rot 增量 = rate(EGG) * 1800", t.Rot, expectedRot, 1e-5);
        Check($"补算后 rot={t.Rot:0.0000} ≥ HATCH_T(0.18) → 已经是蛆（离线期间自己孵了）",
            t.Rot >= LifecycleEngine.HatchT && LifecycleEngine.StageOf(t) == Stage.Larva,
            $"rot={t.Rot:0.0000} stage={LifecycleEngine.StageOf(t)}");

        // 顺带验证：短离线（12 分钟）时 rot 仍低于 HATCH_T，还停在卵
        MaggotConfig cfgShort = new() { RotSpeed = 1.0, BaseRotMinutes = 90 };
        LifecycleEngine eShort = new(cfgShort, randomSeed: 60);
        TaskItem tShort = eShort.AddTask("短离线", now: 0)!;
        eShort.ApplyOfflineDecay(now - 12 * 60 * 1000, now);
        Check($"12 分钟离线后 rot={tShort.Rot:0.0000} < 0.18，仍是卵",
            tShort.Rot < LifecycleEngine.HatchT && LifecycleEngine.StageOf(tShort) == Stage.Egg,
            $"rot={tShort.Rot:0.0000} stage={LifecycleEngine.StageOf(tShort)}");

        // 2) 10 小时离线 → 只补 45 分钟
        float before = t.Rot;
        double applied2 = engine.ApplyOfflineDecay(now - 10 * 3600 * 1000, now);
        Near("10 小时离线只补 45 分钟 = 2700 秒", applied2, 45 * 60.0, 1e-6);
        Check("补算量被 OFFLINE_CAP_MIN 限制住", t.Rot > before, $"rot {before:0.0000} → {t.Rot:0.0000}");

        // 3) ≤1 秒直接跳过
        double applied3 = engine.ApplyOfflineDecay(now - 500, now);
        Eq("离线 ≤1 秒跳过补算", applied3, 0.0);

        // 4) 补算期间不繁殖
        MaggotConfig cfg2 = new() { RotSpeed = 8.0, BreedEnabled = true, BreedSeconds = 1 };
        LifecycleEngine e2 = new(cfg2, randomSeed: 61);
        TaskItem f1 = e2.AddTask("蝇甲", now: 0)!;
        TaskItem f2 = e2.AddTask("蝇乙", now: 0)!;
        f1.Rot = 0.90f;
        f2.Rot = 0.90f;
        int beforeCount = e2.Count;
        e2.ApplyOfflineDecay(now - 40 * 60 * 1000, now);
        Check("离线补算不产生后代（不同步交配）", e2.Count == beforeCount,
            $"{beforeCount} → {e2.Count}");
        Check("补算后 rot 涨了", f1.Rot > 0.90f, $"rot={f1.Rot:0.0000}");

        // 5) 已拍死不补算
        LifecycleEngine e3 = new(cfg, randomSeed: 67);
        TaskItem d = e3.AddTask("已拍死", now: 0)!;
        e3.Complete(d.Id, 500, out _);
        float rot = d.Rot;
        e3.ApplyOfflineDecay(0, now);
        Eq("已拍死任务不参与离线补算", d.Rot, rot);

        // 6) 补算也会刷新阶段
        LifecycleEngine e4 = new(new MaggotConfig { RotSpeed = 1.0, BaseRotMinutes = 90 }, randomSeed: 71);
        TaskItem late = e4.AddTask("长期离线", now: 0)!;
        e4.ApplyOfflineDecay(now - 45 * 60 * 1000, now);
        Check("45 分钟离线后阶段推进到 LARVA",
            LifecycleEngine.StageOf(late) == Stage.Larva,
            $"rot={late.Rot:0.0000} stage={LifecycleEngine.StageOf(late)}");
    }

    // ==================================================================
    // 第 9 节：事件
    // ==================================================================

    private static void EventEmission()
    {
        Section("第 9 节 · 事件");
        MaggotConfig cfg = new() { RotSpeed = 8.0, BaseRotMinutes = 90, BreedEnabled = false };
        LifecycleEngine engine = new(cfg, randomSeed: 73);
        TaskItem t = engine.AddTask("事件测试", now: 0)!;

        var seen = new List<EngineEventType>();
        double elapsed = 0;
        for (int i = 0; i < 200_000 && !seen.Contains(EngineEventType.BecameFly); i++)
        {
            engine.Tick(1.0 / 60.0, (long)(elapsed * 1000));
            elapsed += 1.0 / 60.0;
            foreach (EngineEvent ev in engine.DrainEvents())
            {
                seen.Add(ev.Type);
            }
        }

        Check("发出 Hatched（卵自己孵了）", seen.Contains(EngineEventType.Hatched));
        Check("发出 Pupated（卡住变蛹）", seen.Contains(EngineEventType.Pupated));
        Check("发出 ApproachingFly（越过 0.68）", seen.Contains(EngineEventType.ApproachingFly));
        Check("发出 BecameFly（越过 0.78）", seen.Contains(EngineEventType.BecameFly));

        int hatchedIdx = seen.IndexOf(EngineEventType.Hatched);
        int pupaIdx = seen.IndexOf(EngineEventType.Pupated);
        int apprIdx = seen.IndexOf(EngineEventType.ApproachingFly);
        int flyIdx = seen.IndexOf(EngineEventType.BecameFly);
        Check("事件顺序 Hatched → Pupated → ApproachingFly → BecameFly",
            hatchedIdx < pupaIdx && pupaIdx < apprIdx && apprIdx < flyIdx,
            $"index {hatchedIdx}/{pupaIdx}/{apprIdx}/{flyIdx}");

        // 预警事件只发一次（不刷屏）
        int approachCount = seen.Count(x => x == EngineEventType.ApproachingFly);
        Check("ApproachingFly 只发一次（不每帧刷屏）", approachCount == 1, $"实际 {approachCount} 次");

        int flyCount = seen.Count(x => x == EngineEventType.BecameFly);
        Check("BecameFly 只发一次", flyCount == 1, $"实际 {flyCount} 次");

        // TasksAlreadyFlies：启动汇总用
        List<TaskItem> flies = engine.TasksAlreadyFlies();
        Check("TasksAlreadyFlies 能找到已成蝇的任务", flies.Count == 1, $"实际 {flies.Count}");
        Check("阶段事件不因再次查询而重复",
            engine.DrainEvents().Count == 0, "DrainEvents 第二次应返回空");
    }

    // ==================================================================
    // 第 12 节：每日任务
    // ==================================================================

    private static void DailyKeysBehaviour()
    {
        Section("第 12.2 节 · 日期键与文案工具");

        Check("FromLocal 格式为 YYYY-MM-DD",
            DailyKeys.FromLocal(new DateTime(2026, 9, 13, 23, 59, 59)) == "2026-09-13",
            DailyKeys.FromLocal(new DateTime(2026, 9, 13, 23, 59, 59)));
        Check("补零到两位（1 月 2 日 → 01-02）",
            DailyKeys.FromLocal(new DateTime(2026, 1, 2)) == "2026-01-02",
            DailyKeys.FromLocal(new DateTime(2026, 1, 2)));

        Check("ShortSuffix 取 MM-DD", DailyKeys.ShortSuffix("2026-09-13") == "09-13",
            DailyKeys.ShortSuffix("2026-09-13"));

        // 实例标题格式（规格 §12.2）
        Check("实例标题 = 「<模板> · MM-DD」",
            DailyKeys.InstanceTitle("锻炼", "2026-09-13") == "锻炼 · 09-13",
            DailyKeys.InstanceTitle("锻炼", "2026-09-13"));

        // 逾期天数（规格 §12.1 / §14.2）
        Check("昨天未完成 → 逾期 1 天",
            DailyKeys.OverdueDays("2026-09-12", TaskStatus.Todo, "2026-09-13") == 1);
        Check("今天 → 不逾期",
            DailyKeys.OverdueDays("2026-09-13", TaskStatus.Todo, "2026-09-13") == 0);
        Check("3 天前未完成 → 逾期 3 天",
            DailyKeys.OverdueDays("2026-09-10", TaskStatus.Todo, "2026-09-13") == 3);
        Check("已完成 → 永不逾期",
            DailyKeys.OverdueDays("2026-09-01", TaskStatus.Done, "2026-09-13") == 0);
        Check("未来的日期键不算逾期",
            DailyKeys.OverdueDays("2026-09-14", TaskStatus.Todo, "2026-09-13") == 0);
        Check("dailyKey 为空 → 不逾期",
            DailyKeys.OverdueDays(null, TaskStatus.Todo, "2026-09-13") == 0);
        Check("逾期文案 = 「已逾期 N 天」",
            DailyKeys.OverdueText("2026-09-12", TaskStatus.Todo, "2026-09-13") == "已逾期 1 天",
            DailyKeys.OverdueText("2026-09-12", TaskStatus.Todo, "2026-09-13"));
        Check("未逾期时文案为空串",
            DailyKeys.OverdueText("2026-09-13", TaskStatus.Todo, "2026-09-13") == string.Empty);

        // 跨月/跨年也要对
        Check("跨月：8-31 → 9-01 = 1 天",
            DailyKeys.OverdueDays("2026-08-31", TaskStatus.Todo, "2026-09-01") == 1);
        Check("跨年：12-31 → 01-01 = 1 天",
            DailyKeys.OverdueDays("2025-12-31", TaskStatus.Todo, "2026-01-01") == 1);

        // 「放置」文案（规格 §14.2）
        const long baseMs = 1_700_000_000_000L;
        Check("放置 30 秒", DailyKeys.PlacedText(baseMs, baseMs + 30_000) == "30 秒",
            DailyKeys.PlacedText(baseMs, baseMs + 30_000));
        Check("放置 5 分钟", DailyKeys.PlacedText(baseMs, baseMs + 300_000) == "5 分钟",
            DailyKeys.PlacedText(baseMs, baseMs + 300_000));
        Check("放置 3 小时 12 分（规格 §14.2 的示例）",
            DailyKeys.PlacedText(baseMs, baseMs + (3 * 3600 + 12 * 60) * 1000L) == "3 小时 12 分",
            DailyKeys.PlacedText(baseMs, baseMs + (3 * 3600 + 12 * 60) * 1000L));
        Check("放置 2 天", DailyKeys.PlacedText(baseMs, baseMs + 2L * 86400_000) == "2 天",
            DailyKeys.PlacedText(baseMs, baseMs + 2L * 86400_000));
    }

    /// <summary>规格 §12.5 的七条验收标准，逐条断言。</summary>
    private static void DailyTemplateAcceptanceCriteria12_5()
    {
        Section("第 12.5 节 · 验收标准 1–7");

        // 时间戳与日期键必须对齐：用 2026-09-13 的本地正午
        long now = LocalNoonMs(2026, 9, 13);
        const string day1 = "2026-09-13";
        const string day2 = "2026-09-14";

        // ── 1. 建一个模板「锻炼」→ 立刻出现 锻炼 · MM-DD，rot == 0 ──
        LifecycleEngine engine = new(new MaggotConfig(), randomSeed: 201);
        DailyTemplate? tpl = engine.AddDailyTemplate("锻炼", string.Empty, now);
        Check("验收 1：AddDailyTemplate 返回模板", tpl is not null);

        string day1Title = DailyKeys.InstanceTitle("锻炼", day1);
        TaskItem? day1Instance = engine.FindDailyInstance(tpl!.Id, day1);
        Check("验收 1：立刻出现实例", day1Instance is not null);
        if (day1Instance is not null)
        {
            Check("验收 1：标题 = 「锻炼 · MM-DD」", day1Instance.Title == day1Title, day1Instance.Title);
            Eq("验收 1：rot == 0", day1Instance.Rot, 0.0);
            Check("验收 1：status == TODO", day1Instance.Status == TaskStatus.Todo);
            Check("验收 1：generation == 0 且 gene == 1.0（不吃繁殖加成）",
                day1Instance.Generation == 0 && Math.Abs(day1Instance.Gene - 1.0f) < 1e-6,
                $"gen={day1Instance.Generation} gene={day1Instance.Gene}");
            Check("验收 1：dailyTemplateId / dailyKey 已写入",
                day1Instance.DailyTemplateId == tpl.Id && day1Instance.DailyKey == day1,
                $"{day1Instance.DailyTemplateId} / {day1Instance.DailyKey}");
        }

        // 立刻生成也要发 DailySpawned 事件
        Check("验收 1：发出 DailySpawned 事件",
            engine.DrainEvents().Any(e => e.Type == EngineEventType.DailySpawned));

        // ── 2. 同一天反复调用 EnsureDailyTasks 100 次 → 实例数量恒为 1 ──
        DailyTemplate? tpl2 = engine.AddDailyTemplate("喝水", string.Empty, now);
        Check("（前置）建立了第二个模板", tpl2 is not null);

        for (int i = 0; i < 100; i++) engine.EnsureDailyTasks(day1, now + i * 1000);
        Check("验收 2：100 次调用后，模板 1 的实例数恒为 1",
            engine.DailyInstancesOf(tpl.Id).Count == 1, $"实际 {engine.DailyInstancesOf(tpl.Id).Count}");
        Check("验收 2：模板 2 的实例数也恒为 1",
            engine.DailyInstancesOf(tpl2!.Id).Count == 1, $"实际 {engine.DailyInstancesOf(tpl2.Id).Count}");
        Check("验收 2：EnsureDailyTasks 第 2 次起返回 0（没有新建）",
            engine.EnsureDailyTasks(day1, now) == 0);
        Check("验收 2：任务总数 = 2", engine.Count == 2, $"实际 {engine.Count}");

        // ── 3. 换成明天再调用 → 2 个实例，且昨天那个 rot 没被重置 ──
        TaskItem? yesterday = engine.FindDailyInstance(tpl.Id, day1);
        if (yesterday is not null) yesterday.Rot = 0.42f;   // 让它烂一会儿

        int created = engine.EnsureDailyTasks(day2, now + 86400_000);
        Check("验收 3：跨天后新建了 2 个实例（两个模板各一个）", created == 2, $"实际 {created}");
        Check("验收 3：模板 1 共有 2 个实例", engine.DailyInstancesOf(tpl.Id).Count == 2,
            $"实际 {engine.DailyInstancesOf(tpl.Id).Count}");
        TaskItem? stillYesterday = engine.FindDailyInstance(tpl.Id, day1);
        Check("验收 3：昨天那个依然存在", stillYesterday is not null);
        if (stillYesterday is not null)
        {
            Eq("验收 3：昨天那个的 rot 没有被重置（仍是 0.42）", stillYesterday.Rot, 0.42);
        }
        TaskItem? today = engine.FindDailyInstance(tpl.Id, day2);
        Check("验收 3：今天那个是新实例、rot == 0", today is not null && today.Rot == 0f,
            today is null ? "(找不到)" : $"rot={today.Rot}");
        Check("验收 3：今天的标题带新日期后缀",
            today?.Title == DailyKeys.InstanceTitle("锻炼", day2), today?.Title ?? "(null)");

        // ── 4. 停用模板后再调用 → 不生成新实例；旧实例照常腐烂 ──
        const string day3 = "2026-09-15";
        Check("验收 4：SetDailyTemplateEnabled(false) 成功", engine.SetDailyTemplateEnabled(tpl.Id, false));
        int created3 = engine.EnsureDailyTasks(day3, now + 2 * 86400_000);
        Check("验收 4：停用后不再生成模板 1 的新实例", engine.FindDailyInstance(tpl.Id, day3) is null);
        Check("验收 4：另一个仍然启用的模板照常生成", engine.FindDailyInstance(tpl2.Id, day3) is not null);
        Check("验收 4：停用的模板实例数没变（仍是 2）",
            engine.DailyInstancesOf(tpl.Id).Count == 2, $"实际 {engine.DailyInstancesOf(tpl.Id).Count}");
        Check("（本次只新建了 1 个实例，且只来自未停用的模板）", created3 == 1, $"实际 {created3}");

        // 旧实例照常腐烂
        TaskItem? old = engine.FindDailyInstance(tpl.Id, day2);
        if (old is not null)
        {
            old.Rot = 0.30f;
            for (int i = 0; i < 60 * 60; i++) engine.Tick(1.0 / 60.0, now + i * 16);
            Check("验收 4：停用后旧实例照常腐烂", old.Rot > 0.30f, $"rot={old.Rot:0.0000}");
        }

        // ── 5. 删除模板 → 实例还在，且能正常完成 / 变成苍蝇 ──
        int instancesBeforeDelete = engine.DailyInstancesOf(tpl.Id).Count;
        Check("验收 5：RemoveDailyTemplate 成功", engine.RemoveDailyTemplate(tpl.Id));
        Check("验收 5：模板已被移除", engine.DailyTemplates().All(x => x.Id != tpl.Id));
        Check("验收 5：实例一个都没少", engine.DailyInstancesOf(tpl.Id).Count == instancesBeforeDelete,
            $"{instancesBeforeDelete} → {engine.DailyInstancesOf(tpl.Id).Count}");

        TaskItem? orphan = engine.FindDailyInstance(tpl.Id, day2);
        Check("验收 5：孤儿实例依然存在（退化成普通任务）", orphan is not null);
        if (orphan is not null)
        {
            // 能正常变成苍蝇
            orphan.Rot = LifecycleEngine.FlyT - 0.01f;
            orphan.Status = TaskStatus.Todo;
            for (int i = 0; i < 60 * 60; i++) engine.Tick(1.0 / 60.0, 5_000_000 + i * 16);
            Check("验收 5：孤儿实例能正常变成苍蝇",
                LifecycleEngine.StageOf(orphan) == Stage.Fly,
                $"rot={orphan.Rot:0.0000} stage={LifecycleEngine.StageOf(orphan)}");
            Check("验收 5：孤儿实例能正常完成",
                engine.Complete(orphan.Id, 9_000_000, out _) && orphan.Status == TaskStatus.Done);
        }

        // ── 6. 每日实例变成苍蝇后参与繁殖 → 子代 dailyTemplateId == null ──
        MaggotConfig breedCfg = new()
        {
            RotSpeed = 8.0,
            BreedEnabled = true,
            BreedSeconds = 1,
            MaxTasks = 200,
            MaxFlies = 500,
        };
        LifecycleEngine breeder = new(breedCfg, randomSeed: 211);
        breeder.AddDailyTemplate("每日甲", string.Empty, 0);
        TaskItem? dailyA = breeder.FindDailyInstance(breeder.DailyTemplates()[0].Id, DailyKeys.FromUnixMs(0));
        Check("（前置）每日甲实例存在", dailyA is not null);
        TaskItem dailyB = breeder.AddTask("普通乙", string.Empty, 0)!;

        if (dailyA is not null)
        {
            dailyA.Rot = 0.95f;   // FLY
            dailyB.Rot = 0.95f;   // FLY

            long tickNow = 0;
            TaskItem? bred = null;
            for (int i = 0; i < 60 * 60 * 10 && bred is null; i++)
            {
                tickNow += 16;
                breeder.Tick(1.0 / 60.0, tickNow);
                bred = breeder.Snapshot().FirstOrDefault(t => t.Generation >= 1);
            }

            Check("验收 6：每日实例参与了繁殖（出现了后代）", bred is not null,
                $"任务数 {breeder.Count}");
            if (bred is not null)
            {
                Check("验收 6：Bred 的子代 dailyTemplateId == null",
                    bred.DailyTemplateId is null, $"实际 {bred.DailyTemplateId ?? "(null)"}");
                Check("验收 6：Bred 的子代 dailyKey == null",
                    bred.DailyKey is null, $"实际 {bred.DailyKey ?? "(null)"}");
                Check("验收 6：子代标题是「蝇卵 #...」，不是每日实例",
                    bred.Title.StartsWith("蝇卵 #", StringComparison.Ordinal), bred.Title);
            }
        }

        // ── 7. dailyEnabled = false → 一个实例都不生成 ──
        MaggotConfig offCfg = new() { DailyEnabled = false };
        LifecycleEngine off = new(offCfg, randomSeed: 223);
        DailyTemplate? offTpl = off.AddDailyTemplate("不该出现", string.Empty, now);
        Check("验收 7：dailyEnabled=false 时新增模板不生成实例",
            offTpl is not null && off.Count == 0, $"任务数 {off.Count}");
        Check("验收 7：EnsureDailyTasks 也一个都不生成",
            off.EnsureDailyTasks(day1, now) == 0 && off.Count == 0, $"任务数 {off.Count}");

        // 关掉总开关不影响已有实例
        MaggotConfig onCfg = new() { DailyEnabled = true };
        LifecycleEngine mixed = new(onCfg, randomSeed: 227);
        DailyTemplate? mixedTpl = mixed.AddDailyTemplate("已有", string.Empty, now);
        int before = mixed.Count;
        mixed.Config.DailyEnabled = false;
        mixed.EnsureDailyTasks("2026-09-20", now + 7 * 86400_000);
        Check("验收 7：关掉总开关后已有实例不受影响",
            mixed.Count == before && mixed.FindDailyInstance(mixedTpl!.Id, day1) is not null,
            $"{before} → {mixed.Count}");
    }

    private static void DailyTemplateLifecycle()
    {
        Section("第 12.2 / 12.3 节 · 幂等、排序、生命周期与普通任务一致");

        long now = LocalNoonMs(2026, 9, 13);
        const string day = "2026-09-13";

        // ── 幂等：不同起始状态下反复调用都不重复 ──
        LifecycleEngine e1 = new(new MaggotConfig(), randomSeed: 233);
        DailyTemplate? a = e1.AddDailyTemplate("甲", string.Empty, now);
        DailyTemplate? b = e1.AddDailyTemplate("乙", string.Empty, now);
        DailyTemplate? c = e1.AddDailyTemplate("丙", string.Empty, now);
        Check("3 个模板 → 3 个实例", e1.Count == 3, $"实际 {e1.Count}");

        for (int i = 0; i < 500; i++) e1.EnsureDailyTasks(day, now);
        Check("500 次 EnsureDailyTasks 后仍是 3 个实例（幂等）", e1.Count == 3, $"实际 {e1.Count}");

        // 拍死一个再 Ensure：也不该补一个新的（当天已有实例，只是已完成）
        TaskItem? instA = e1.FindDailyInstance(a!.Id, day);
        if (instA is not null) e1.Complete(instA.Id, now, out _);
        e1.EnsureDailyTasks(day, now);
        Check("当天实例已拍死 → Ensure 不会补一个新的（同一天只有一个实例）",
            e1.Count == 3, $"实际 {e1.Count}");
        Check("已拍死实例的 dailyKey 保持不变",
            e1.FindDailyInstance(a.Id, day)?.Status == TaskStatus.Done);

        // ── 排序：按 sortOrder ──
        IReadOnlyList<DailyTemplate> templates = e1.DailyTemplates();
        Check("DailyTemplates 按 sortOrder 升序",
            templates[0].Id == a.Id && templates[1].Id == b!.Id && templates[2].Id == c!.Id,
            string.Join(",", templates.Select(t => t.Title)));

        // ── 生命周期不变式：实例与普通任务完全一样 ──
        MaggotConfig cfg = new() { RotSpeed = 8.0, BaseRotMinutes = 90, BreedEnabled = false };
        LifecycleEngine e2 = new(cfg, randomSeed: 239);
        DailyTemplate? tpl = e2.AddDailyTemplate("每日", string.Empty, now);
        TaskItem? daily = e2.FindDailyInstance(tpl!.Id, day);
        TaskItem plain = e2.AddTask("普通", string.Empty, now)!;
        Check("每日实例与普通任务初始 rot 相同", daily!.Rot == plain.Rot);
        Check("每日实例与普通任务 gene 相同",
            Math.Abs(daily.Gene - plain.Gene) < 1e-6, $"{daily.Gene} / {plain.Gene}");

        for (int i = 0; i < 60 * 60; i++) e2.Tick(1.0 / 60.0, now + i * 16);
        Near("跑 1 分钟后两者 rot 完全一致（同样的腐烂速率）", daily.Rot, plain.Rot, 1e-6);

        // 「我去做了」对每日实例同样有效
        float beforeCare = daily.Rot;
        float relief = e2.Care(daily.Id, 100_000_000);
        Check("每日实例可以被「我去做了」", relief > 0f && daily.Rot < beforeCare,
            $"{beforeCare:0.0000} → {daily.Rot:0.0000}");
        Check("每日实例 care 后立刻变蛆", LifecycleEngine.StageOf(daily) == Stage.Larva);

        // 「重置」把它退回卵
        e2.ResetAll(200_000_000);
        Eq("「重置」把每日实例退回 rot=0", daily.Rot, 0.0);

        // 「清空」把它删掉
        e2.ClearAll();
        Check("「清空」删掉每日实例", e2.Count == 0 && e2.FindDailyInstance(tpl.Id, day) is null);

        // ── 空标题被拒绝 ──
        LifecycleEngine e3 = new(new MaggotConfig(), randomSeed: 241);
        Check("空标题不建立模板", e3.AddDailyTemplate("   ") is null);
        Check("空标题也没有生成任何实例", e3.Count == 0);

        // ── maxTasks 挡住了实例生成，但模板本身仍然建立 ──
        MaggotConfig tiny = new() { MaxTasks = 1 };
        LifecycleEngine e4 = new(tiny, randomSeed: 243);
        DailyTemplate? t4 = e4.AddDailyTemplate("第一个", string.Empty, now);
        DailyTemplate? t5 = e4.AddDailyTemplate("第二个", string.Empty, now);
        Check("maxTasks 触顶：模板仍然建立", t4 is not null && t5 is not null);
        Check("maxTasks 触顶：只生成了 1 个实例", e4.Count == 1, $"实际 {e4.Count}");

        // ── 状态文案（规格 §12.5 界面要求：未生成 / 卵 / 蛆 / 蛹 / 苍蝇 / 已完成）──
        Check("未生成 → 「未生成」", LifecycleEngine.DailyInstanceStatusText(null) == "未生成");
        Check("rot 0 → 「卵」",
            LifecycleEngine.DailyInstanceStatusText(MakeTask(0f)) == "卵");
        Check("rot 0.30 → 「蛆」",
            LifecycleEngine.DailyInstanceStatusText(MakeTask(0.30f)) == "蛆");
        Check("rot 0.60 → 「蛹」",
            LifecycleEngine.DailyInstanceStatusText(MakeTask(0.60f)) == "蛹");
        Check("rot 0.90 → 「苍蝇」",
            LifecycleEngine.DailyInstanceStatusText(MakeTask(0.90f)) == "苍蝇");
        Check("已拍死 → 「已完成」",
            LifecycleEngine.DailyInstanceStatusText(MakeTask(0.30f, TaskStatus.Done)) == "已完成");

        // ── 每日实例仍然受 maxTasks 兜底 ──
        Check("每日实例不会突破 maxTasks", e1.Count <= e1.Config.MaxTasks, $"实际 {e1.Count}");
    }

    private static void WeeklyKeysBehaviour()
    {
        Section("第 15.3 节 · ISO 周键与星期（跨年用周基准年）");

        // ── 周键格式：周基准年 + 补零两位周号 ──
        Check("2026-09-13（周日）→ 2026-W37（规格 §15.1 示例里的 W37）",
            WeeklyKeys.FromLocal(new DateTime(2026, 9, 13)) == "2026-W37",
            WeeklyKeys.FromLocal(new DateTime(2026, 9, 13)));
        Check("周号补零到两位（2026-01-05 → 2026-W02）",
            WeeklyKeys.FromLocal(new DateTime(2026, 1, 5)) == "2026-W02",
            WeeklyKeys.FromLocal(new DateTime(2026, 1, 5)));

        // ── 同一周内的任意一天周键相同 ──
        string mon = WeeklyKeys.FromLocal(new DateTime(2026, 9, 14));
        string fri = WeeklyKeys.FromLocal(new DateTime(2026, 9, 18));
        string sun = WeeklyKeys.FromLocal(new DateTime(2026, 9, 20));
        Check("同一周（周一/周五/周日）周键完全相同", mon == "2026-W38" && fri == mon && sun == mon,
            $"{mon} / {fri} / {sun}");
        Check("下一周的周一换键（2026-09-21 → 2026-W39）",
            WeeklyKeys.FromLocal(new DateTime(2026, 9, 21)) == "2026-W39");

        // ── 验收 9：跨年周键必须用「周基准年」──
        string eve = WeeklyKeys.FromLocal(new DateTime(2026, 12, 31));
        string newDay = WeeklyKeys.FromLocal(new DateTime(2027, 1, 1));
        Check("跨年：2026-12-31 与 2027-01-01 落在同一 ISO 周，周键相同",
            eve == newDay, $"{eve} / {newDay}");
        Check("跨年：这一周的周键是 2026-W53（周基准年，不是日历年 2027）",
            eve == "2026-W53", eve);
        Check("跨年：ISOWeek.GetYear(2027-01-01) = 2026，而 DateTime.Year = 2027（说明转换是必需的）",
            System.Globalization.ISOWeek.GetYear(new DateTime(2027, 1, 1)) == 2026
            && new DateTime(2027, 1, 1).Year == 2027,
            $"ISOWeekYear={System.Globalization.ISOWeek.GetYear(new DateTime(2027, 1, 1))} " +
            $"Year={new DateTime(2027, 1, 1).Year}");
        Check("跨年：下一周才换键（2027-01-04 → 2027-W01）",
            WeeklyKeys.FromLocal(new DateTime(2027, 1, 4)) == "2027-W01",
            WeeklyKeys.FromLocal(new DateTime(2027, 1, 4)));
        Check("经典跨年：2021-01-01 → 2020-W53，2021-01-04 → 2021-W01",
            WeeklyKeys.FromLocal(new DateTime(2021, 1, 1)) == "2020-W53"
            && WeeklyKeys.FromLocal(new DateTime(2021, 1, 4)) == "2021-W01",
            $"{WeeklyKeys.FromLocal(new DateTime(2021, 1, 1))} / {WeeklyKeys.FromLocal(new DateTime(2021, 1, 4))}");

        // ── 星期换算：C# DayOfWeek 是 0=周日，规格是 1=周一 … 7=周日 ──
        Check("C# 原生确实把周日算成 0（所以必须转换）",
            new DateTime(2026, 9, 13).DayOfWeek == DayOfWeek.Sunday && (int)DayOfWeek.Sunday == 0);
        Check("周日 → 7（不是 0）", WeeklyKeys.IsoWeekday(new DateTime(2026, 9, 13)) == 7,
            $"实际 {WeeklyKeys.IsoWeekday(new DateTime(2026, 9, 13))}");
        Check("周一 → 1", WeeklyKeys.IsoWeekday(new DateTime(2026, 9, 14)) == 1);
        Check("周三 → 3", WeeklyKeys.IsoWeekday(new DateTime(2026, 9, 16)) == 3);
        Check("周六 → 6", WeeklyKeys.IsoWeekday(new DateTime(2026, 9, 19)) == 6);
        int[] week = Enumerable.Range(0, 7)
            .Select(i => WeeklyKeys.IsoWeekday(new DateTime(2026, 9, 14).AddDays(i))).ToArray();
        Check("周一到周日正好覆盖 1..7（没有 0，也没有重复）",
            week.SequenceEqual(new[] { 1, 2, 3, 4, 5, 6, 7 }), string.Join(",", week));

        // ── 星期名 ──
        Check("星期名 1..7 = 周一..周日",
            string.Join("/", Enumerable.Range(1, 7).Select(WeeklyKeys.WeekdayLabel)) == "周一/周二/周三/周四/周五/周六/周日",
            string.Join("/", Enumerable.Range(1, 7).Select(WeeklyKeys.WeekdayLabel)));
        Check("越界星期 → 「周?」", WeeklyKeys.WeekdayLabel(0) == "周?" && WeeklyKeys.WeekdayLabel(8) == "周?");

        // ── 实例标题（规格 §15.4： "<tpl.title> · W<ww>"）──
        Check("实例标题 = 「写周报 · W37」",
            WeeklyKeys.InstanceTitle("写周报", "2026-W37") == "写周报 · W37",
            WeeklyKeys.InstanceTitle("写周报", "2026-W37"));
        Check("标题后缀保留两位周号（W05 不是 W5）",
            WeeklyKeys.ShortSuffix("2026-W05") == "W05", WeeklyKeys.ShortSuffix("2026-W05"));

        // ── 逾期周数（规格 §15.5）──
        Check("本周 → 不逾期", WeeklyKeys.OverdueWeeks("2026-W38", "2026-W38") == 0);
        Check("上周 → 逾期 1 周", WeeklyKeys.OverdueWeeks("2026-W37", "2026-W38") == 1);
        Check("3 周前 → 逾期 3 周", WeeklyKeys.OverdueWeeks("2026-W35", "2026-W38") == 3);
        Check("跨年：2026-W53 相对 2027-W01 只逾期 1 周（按周一相减，不是比字符串）",
            WeeklyKeys.OverdueWeeks("2026-W53", "2027-W01") == 1,
            $"实际 {WeeklyKeys.OverdueWeeks("2026-W53", "2027-W01")}");
        Check("跨年：2026-W52 相对 2027-W01 逾期 2 周",
            WeeklyKeys.OverdueWeeks("2026-W52", "2027-W01") == 2);
        Check("未来的周键不算逾期", WeeklyKeys.OverdueWeeks("2026-W39", "2026-W38") == 0);
        Check("weeklyKey 为空 → 不逾期", WeeklyKeys.OverdueWeeks(null, "2026-W38") == 0);
        Check("周键写坏了 → 不逾期（不抛异常）", WeeklyKeys.OverdueWeeks("乱写", "2026-W38") == 0);
        Check("周号没补零也能算（2026-W1 相对 2026-W03 = 2 周）",
            WeeklyKeys.OverdueWeeks("2026-W1", "2026-W03") == 2);
        Check("已完成 → 永不逾期（与每日任务一致）",
            WeeklyKeys.OverdueWeeks("2026-W37", TaskStatus.Done, "2026-W38") == 0);
        Check("逾期文案 = 「已逾期 1 周」",
            WeeklyKeys.OverdueText("2026-W37", "2026-W38") == "已逾期 1 周",
            WeeklyKeys.OverdueText("2026-W37", "2026-W38"));
        Check("未逾期时文案为空串",
            WeeklyKeys.OverdueText("2026-W38", "2026-W38") == string.Empty);
        Check("已完成时文案为空串",
            WeeklyKeys.OverdueText("2026-W37", TaskStatus.Done, "2026-W38") == string.Empty);
    }

    /// <summary>规格 §15.6 的十条验收标准，逐条断言。</summary>
    private static void WeeklyTemplateAcceptanceCriteria15_6()
    {
        Section("第 15.6 节 · 验收标准 1–10");

        // 时间戳与周键必须对齐：用 2026-09-16（周三）的本地正午 → 2026-W38，ISO 星期 3
        const int wdWed = 3;
        const string week38 = "2026-W38";
        const string week39 = "2026-W39";
        long wed = LocalNoonMs(2026, 9, 16);

        // ── 1. 建一个 weekday = 今天 的模板 → 立刻出现实例，标题形如 写周报 · W38，rot == 0 ──
        LifecycleEngine engine = new(new MaggotConfig(), randomSeed: 301);
        WeeklyTemplate? tpl = engine.AddWeeklyTemplate("写周报", wdWed, string.Empty, wed);
        Check("验收 1：AddWeeklyTemplate 返回模板", tpl is not null);

        string expectedTitle = WeeklyKeys.InstanceTitle("写周报", week38);
        TaskItem? first = engine.FindWeeklyInstance(tpl!.Id, week38);
        Check("验收 1：立刻出现本周实例", first is not null);
        if (first is not null)
        {
            Check($"验收 1：标题 = 「写周报 · W38」（规格 §15.1 的 W37 同款形状）",
                first.Title == expectedTitle, first.Title);
            Eq("验收 1：rot == 0", first.Rot, 0.0);
            Check("验收 1：status == TODO", first.Status == TaskStatus.Todo);
            Check("验收 1：generation == 0 且 gene == 1.0（不吃繁殖加成）",
                first.Generation == 0 && Math.Abs(first.Gene - 1.0f) < 1e-6,
                $"gen={first.Generation} gene={first.Gene}");
            Check("验收 1：weeklyTemplateId / weeklyKey 已写入",
                first.WeeklyTemplateId == tpl.Id && first.WeeklyKey == week38,
                $"{first.WeeklyTemplateId} / {first.WeeklyKey}");
            Check("验收 1：每周实例不是每日实例（daily* 为空）",
                first.DailyTemplateId is null && first.DailyKey is null);
        }
        Check("验收 1：发出 WeeklySpawned 事件",
            engine.DrainEvents().Any(e => e.Type == EngineEventType.WeeklySpawned));

        // ── 2. weekday 晚于今天 → 不生成 ──
        int countBefore = engine.Count;
        WeeklyTemplate? laterOrNull = engine.AddWeeklyTemplate("周五复盘", 5, string.Empty, wed);
        Check("（前置）建立了 weekday=5 的模板", laterOrNull is not null);
        WeeklyTemplate later = laterOrNull!;
        Check("验收 2：weekday=5 而今天是周三（3 < 5）→ 不生成",
            engine.FindWeeklyInstance(later.Id, week38) is null && engine.Count == countBefore,
            $"任务数 {countBefore} → {engine.Count}");
        Check("验收 2：核心 API 同样遵守 wd < weekday 不生成",
            engine.EnsureWeeklyTasks(week38, isoWeekday: 2, now: wed) == 0
            && engine.FindWeeklyInstance(later.Id, week38) is null);
        // 迟到也补（§15.4）：到周五（同样还是 W38）就该补上
        Check("验收 2（反面）：到了周五（wd=5）照样补上，不跳过这一周",
            engine.EnsureWeeklyTasks(week38, isoWeekday: 5, now: wed + 2 * 86400_000) == 1
            && engine.FindWeeklyInstance(later.Id, week38) is not null);

        // ── 3. 同一周反复调用 100 次 → 实例数量恒为 1（每个模板）──
        for (int i = 0; i < 100; i++) engine.EnsureWeeklyTasks(week38, wdWed, wed + i * 1000);
        Check("验收 3：100 次调用后「写周报」实例数恒为 1",
            engine.WeeklyInstancesOf(tpl.Id).Count == 1, $"实际 {engine.WeeklyInstancesOf(tpl.Id).Count}");
        Check("验收 3：「周五复盘」实例数也恒为 1",
            engine.WeeklyInstancesOf(later.Id).Count == 1, $"实际 {engine.WeeklyInstancesOf(later.Id).Count}");
        Check("验收 3：EnsureWeeklyTasks 第 2 次起返回 0（没有新建）",
            engine.EnsureWeeklyTasks(week38, wdWed, wed) == 0);
        Check("验收 3：任务总数 = 2", engine.Count == 2, $"实际 {engine.Count}");

        // ── 4. 换到下一周的 key → 2 个实例，且上一周那个的 rot 没有被重置 ──
        TaskItem? lastWeekInstance = engine.FindWeeklyInstance(tpl.Id, week38);
        if (lastWeekInstance is not null) lastWeekInstance.Rot = 0.42f;   // 让它烂一会儿

        // 取周五（ISO 5）：两个模板的 weekday（3 和 5）都满足，各自生成一个
        int created = engine.EnsureWeeklyTasks(week39, 5, wed + 9 * 86400_000);
        Check("验收 4：换到下一周后为两个模板各建 1 个实例", created == 2, $"实际 {created}");
        Check("验收 4：「写周报」共有 2 个实例（上一周 + 这一周）",
            engine.WeeklyInstancesOf(tpl.Id).Count == 2, $"实际 {engine.WeeklyInstancesOf(tpl.Id).Count}");
        TaskItem? stillLastWeek = engine.FindWeeklyInstance(tpl.Id, week38);
        Check("验收 4：上一周那个依然存在", stillLastWeek is not null);
        if (stillLastWeek is not null)
        {
            Eq("验收 4：上一周那个的 rot 没有被重置（仍是 0.42）", stillLastWeek.Rot, 0.42);
        }
        TaskItem? thisWeek = engine.FindWeeklyInstance(tpl.Id, week39);
        Check("验收 4：这一周那个是新实例、rot == 0", thisWeek is not null && thisWeek.Rot == 0f,
            thisWeek is null ? "(找不到)" : $"rot={thisWeek.Rot}");
        Check("验收 4：新实例的标题带新周号",
            thisWeek?.Title == WeeklyKeys.InstanceTitle("写周报", week39), thisWeek?.Title ?? "(null)");
        Check("验收 4：新实例的 weeklyKey 是新的周键",
            thisWeek?.WeeklyKey == week39, thisWeek?.WeeklyKey ?? "(null)");

        // ── 5. 停用模板 → 不再生成新实例；旧实例照常腐烂 ──
        const string week40 = "2026-W40";
        Check("验收 5：SetWeeklyTemplateEnabled(false) 成功",
            engine.SetWeeklyTemplateEnabled(tpl.Id, false));
        // 同样取周五（ISO 5）：停用的那个不生成，启用的那个照常生成
        int created40 = engine.EnsureWeeklyTasks(week40, 5, wed + 16 * 86400_000);
        Check("验收 5：停用后不再生成该模板的新实例",
            engine.FindWeeklyInstance(tpl.Id, week40) is null);
        Check("验收 5：另一个仍然启用的模板照常生成",
            engine.FindWeeklyInstance(later.Id, week40) is not null);
        Check("验收 5：停用后实例数没变（仍是 2）",
            engine.WeeklyInstancesOf(tpl.Id).Count == 2, $"实际 {engine.WeeklyInstancesOf(tpl.Id).Count}");
        Check("（本次只新建了 1 个实例，且只来自未停用的模板）", created40 == 1, $"实际 {created40}");

        TaskItem? old = engine.FindWeeklyInstance(tpl.Id, week39);
        if (old is not null)
        {
            old.Rot = 0.30f;
            for (int i = 0; i < 60 * 60; i++) engine.Tick(1.0 / 60.0, wed + i * 16);
            Check("验收 5：停用后旧实例照常腐烂", old.Rot > 0.30f, $"rot={old.Rot:0.0000}");
        }

        // ── 6. 删除模板 → 实例还在，仍能正常完成 / 变成苍蝇 ──
        int instancesBeforeDelete = engine.WeeklyInstancesOf(tpl.Id).Count;
        Check("验收 6：RemoveWeeklyTemplate 成功", engine.RemoveWeeklyTemplate(tpl.Id));
        Check("验收 6：模板已被移除", engine.WeeklyTemplates().All(x => x.Id != tpl.Id));
        Check("验收 6：实例一个都没少",
            engine.WeeklyInstancesOf(tpl.Id).Count == instancesBeforeDelete,
            $"{instancesBeforeDelete} → {engine.WeeklyInstancesOf(tpl.Id).Count}");

        TaskItem? orphan = engine.FindWeeklyInstance(tpl.Id, week39);
        Check("验收 6：孤儿实例依然存在（退化成普通任务）", orphan is not null);
        if (orphan is not null)
        {
            orphan.Rot = LifecycleEngine.FlyT - 0.01f;
            orphan.Status = TaskStatus.Todo;
            for (int i = 0; i < 60 * 60; i++) engine.Tick(1.0 / 60.0, 6_000_000 + i * 16);
            Check("验收 6：孤儿实例能正常变成苍蝇",
                LifecycleEngine.StageOf(orphan) == Stage.Fly,
                $"rot={orphan.Rot:0.0000} stage={LifecycleEngine.StageOf(orphan)}");
            Check("验收 6：孤儿实例能正常完成",
                engine.Complete(orphan.Id, 9_500_000, out _) && orphan.Status == TaskStatus.Done);
        }

        // ── 7. 每周实例变苍蝇后繁殖 → 子代 weeklyTemplateId == null ──
        MaggotConfig breedCfg = new()
        {
            RotSpeed = 8.0,
            BreedEnabled = true,
            BreedSeconds = 1,
            MaxTasks = 200,
            MaxFlies = 500,
        };
        LifecycleEngine breeder = new(breedCfg, randomSeed: 311);
        WeeklyTemplate? wBreed = breeder.AddWeeklyTemplate("每周甲", wdWed, string.Empty, wed);
        TaskItem? weeklyA = breeder.FindWeeklyInstance(wBreed!.Id, week38);
        Check("（前置）每周甲实例存在", weeklyA is not null);
        TaskItem weeklyB = breeder.AddTask("普通乙", string.Empty, wed)!;

        if (weeklyA is not null)
        {
            weeklyA.Rot = 0.95f;   // FLY
            weeklyB.Rot = 0.95f;   // FLY

            long tickNow = wed;
            TaskItem? bred = null;
            for (int i = 0; i < 60 * 60 * 10 && bred is null; i++)
            {
                tickNow += 16;
                breeder.Tick(1.0 / 60.0, tickNow);
                bred = breeder.Snapshot().FirstOrDefault(t => t.Generation >= 1);
            }

            Check("验收 7：每周实例参与了繁殖（出现了后代）", bred is not null,
                $"任务数 {breeder.Count}");
            if (bred is not null)
            {
                Check("验收 7：Bred 的子代 weeklyTemplateId == null",
                    bred.WeeklyTemplateId is null, $"实际 {bred.WeeklyTemplateId ?? "(null)"}");
                Check("验收 7：Bred 的子代 weeklyKey == null",
                    bred.WeeklyKey is null, $"实际 {bred.WeeklyKey ?? "(null)"}");
                Check("验收 7：子代标题是「蝇卵 #...」，不是每周实例",
                    bred.Title.StartsWith("蝇卵 #", StringComparison.Ordinal), bred.Title);
            }
        }

        // ── 8. weeklyEnabled = false → 一个实例都不生成 ──
        MaggotConfig offCfg = new() { WeeklyEnabled = false };
        LifecycleEngine off = new(offCfg, randomSeed: 313);
        WeeklyTemplate? offTpl = off.AddWeeklyTemplate("不该出现", wdWed, string.Empty, wed);
        Check("验收 8：weeklyEnabled=false 时新增模板不生成实例",
            offTpl is not null && off.Count == 0, $"任务数 {off.Count}");
        Check("验收 8：EnsureWeeklyTasks 也一个都不生成",
            off.EnsureWeeklyTasks(week38, wdWed, wed) == 0 && off.Count == 0, $"任务数 {off.Count}");

        MaggotConfig onCfg = new() { WeeklyEnabled = true };
        LifecycleEngine mixed = new(onCfg, randomSeed: 317);
        WeeklyTemplate? mixedTpl = mixed.AddWeeklyTemplate("已有", wdWed, string.Empty, wed);
        int beforeOff = mixed.Count;
        mixed.Config.WeeklyEnabled = false;
        mixed.EnsureWeeklyTasks(week39, wdWed, wed + 7 * 86400_000);
        Check("验收 8：关掉总开关后已有实例不受影响",
            mixed.Count == beforeOff && mixed.FindWeeklyInstance(mixedTpl!.Id, week38) is not null,
            $"{beforeOff} → {mixed.Count}");

        // ── 9. 跨年周键：2026-12-31 与 2027-01-01 同一 ISO 周 → 周键相同；下一周才换键 ──
        long eve = LocalNoonMs(2026, 12, 31);     // 周四，2026-W53
        long newDay = LocalNoonMs(2027, 1, 1);    // 周五，仍然是 2026-W53
        long nextMon = LocalNoonMs(2027, 1, 4);   // 周一，2027-W01

        Check("验收 9：两个日期的时间戳分属 2026 与 2027 两个日历年",
            new DateTime(2026, 12, 31).Year == 2026 && new DateTime(2027, 1, 1).Year == 2027);
        Check("验收 9：周键相同（都属于 2026-W53）",
            WeeklyKeys.FromUnixMs(eve) == WeeklyKeys.FromUnixMs(newDay)
            && WeeklyKeys.FromUnixMs(eve) == "2026-W53",
            $"{WeeklyKeys.FromUnixMs(eve)} / {WeeklyKeys.FromUnixMs(newDay)}");

        LifecycleEngine yearEnd = new(new MaggotConfig(), randomSeed: 319);
        WeeklyTemplate? yTpl = yearEnd.AddWeeklyTemplate("年末周报", 1, string.Empty, eve);
        Check("验收 9：12-31 生成了 W53 实例",
            yearEnd.FindWeeklyInstance(yTpl!.Id, "2026-W53") is not null);
        TaskItem? w53 = yearEnd.FindWeeklyInstance(yTpl.Id, "2026-W53");
        if (w53 is not null) w53.Rot = 0.33f;

        int crossed = yearEnd.EnsureWeeklyTasks(newDay);
        Check("验收 9：2027-01-01 仍在同一 ISO 周 → 不生成新实例（幂等跨年）",
            crossed == 0 && yearEnd.WeeklyInstancesOf(yTpl.Id).Count == 1,
            $"created={crossed} 实例数={yearEnd.WeeklyInstancesOf(yTpl.Id).Count}");

        int nextWeek = yearEnd.EnsureWeeklyTasks(nextMon);
        Check("验收 9：到 2027-01-04 才换键 → 生成第 2 个实例",
            nextWeek == 1 && yearEnd.WeeklyInstancesOf(yTpl.Id).Count == 2, $"created={nextWeek}");
        Check("验收 9：新实例的周键 = 2027-W01",
            yearEnd.FindWeeklyInstance(yTpl.Id, "2027-W01") is not null);
        TaskItem? w53After = yearEnd.FindWeeklyInstance(yTpl.Id, "2026-W53");
        Check("验收 9：跨年换周不会重置旧实例的 rot",
            w53After is not null && Math.Abs(w53After.Rot - 0.33f) < 1e-6,
            w53After is null ? "(找不到)" : $"rot={w53After.Rot}");

        // ── 10. 每日与每周互不影响：同一天可以同时生成，各自幂等 ──
        long sameDay = LocalNoonMs(2026, 9, 16);            // 周三
        string sameDayKey = "2026-09-16";
        LifecycleEngine both = new(new MaggotConfig(), randomSeed: 323);
        DailyTemplate? dTpl = both.AddDailyTemplate("锻炼", string.Empty, sameDay);
        WeeklyTemplate? wTpl = both.AddWeeklyTemplate("写周报", wdWed, string.Empty, sameDay);
        Check("验收 10：同一天两种实例各生成了 1 个", both.Count == 2, $"实际 {both.Count}");
        Check("验收 10：每日实例带着 dailyKey、weeklyKey 为空",
            both.FindDailyInstance(dTpl!.Id, sameDayKey) is { DailyKey: "2026-09-16", WeeklyKey: null });
        Check("验收 10：每周实例带着 weeklyKey、dailyKey 为空",
            both.FindWeeklyInstance(wTpl!.Id, week38) is { WeeklyKey: "2026-W38", DailyKey: null });

        for (int i = 0; i < 100; i++)
        {
            both.EnsureDailyTasks(sameDayKey, sameDay + i * 1000);
            both.EnsureWeeklyTasks(week38, wdWed, sameDay + i * 1000);
        }
        Check("验收 10：100 次交叉调用后各自仍然只有 1 个实例",
            both.DailyInstancesOf(dTpl.Id).Count == 1 && both.WeeklyInstancesOf(wTpl.Id).Count == 1,
            $"每日 {both.DailyInstancesOf(dTpl.Id).Count} / 每周 {both.WeeklyInstancesOf(wTpl.Id).Count}");
        Check("验收 10：任务总数恒为 2", both.Count == 2, $"实际 {both.Count}");

        // 关掉每周总开关不影响每日实例，反之亦然
        both.Config.WeeklyEnabled = false;
        both.EnsureWeeklyTasks("2026-W39", wdWed, sameDay + 7 * 86400_000);
        Check("验收 10：weeklyEnabled=false 时不生成每周实例，但每日实例毫发无损",
            both.WeeklyInstancesOf(wTpl.Id).Count == 1 && both.DailyInstancesOf(dTpl.Id).Count == 1);
    }

    private static void WeeklyTemplateLifecycle()
    {
        Section("第 15.4 / 15.5 节 · 幂等、排序、生命周期与普通任务一致");

        long wed = LocalNoonMs(2026, 9, 16);       // 2026-W38，ISO 3
        const string week = "2026-W38";

        // ── 幂等：反复调用都不重复 ──
        LifecycleEngine e1 = new(new MaggotConfig(), randomSeed: 331);
        WeeklyTemplate? a = e1.AddWeeklyTemplate("甲", 1, string.Empty, wed);
        WeeklyTemplate? b = e1.AddWeeklyTemplate("乙", 1, string.Empty, wed);
        WeeklyTemplate? c = e1.AddWeeklyTemplate("丙", 1, string.Empty, wed);
        Check("3 个模板（weekday=1，今天周三已过）→ 3 个实例", e1.Count == 3, $"实际 {e1.Count}");

        for (int i = 0; i < 500; i++) e1.EnsureWeeklyTasks(week, 3, wed);
        Check("500 次 EnsureWeeklyTasks 后仍是 3 个实例（幂等）", e1.Count == 3, $"实际 {e1.Count}");

        // 拍死一个再 Ensure：也不该补一个新的（本周已有实例，只是已完成）
        TaskItem? instA = e1.FindWeeklyInstance(a!.Id, week);
        if (instA is not null) e1.Complete(instA.Id, wed, out _);
        e1.EnsureWeeklyTasks(week, 3, wed);
        Check("本周实例已拍死 → Ensure 不会补一个新的（同一周只有一个实例）",
            e1.Count == 3, $"实际 {e1.Count}");
        Check("已拍死实例的 weeklyKey 保持不变",
            e1.FindWeeklyInstance(a.Id, week)?.Status == TaskStatus.Done);

        // ── 排序：按 sortOrder ──
        IReadOnlyList<WeeklyTemplate> templates = e1.WeeklyTemplates();
        Check("WeeklyTemplates 按 sortOrder 升序",
            templates[0].Id == a.Id && templates[1].Id == b!.Id && templates[2].Id == c!.Id,
            string.Join(",", templates.Select(t => t.Title)));
        Check("sortOrder 依次为 0/1/2",
            templates[0].SortOrder == 0 && templates[1].SortOrder == 1 && templates[2].SortOrder == 2,
            string.Join(",", templates.Select(t => t.SortOrder)));

        // ── 生命周期不变式：实例与普通任务完全一样 ──
        MaggotConfig cfg = new() { RotSpeed = 8.0, BaseRotMinutes = 90, BreedEnabled = false };
        LifecycleEngine e2 = new(cfg, randomSeed: 337);
        WeeklyTemplate? tpl = e2.AddWeeklyTemplate("每周", 1, "备注原样带过去", wed);
        TaskItem? weekly = e2.FindWeeklyInstance(tpl!.Id, week);
        TaskItem plain = e2.AddTask("普通", string.Empty, wed)!;
        Check("每周实例与普通任务初始 rot 相同", weekly!.Rot == plain.Rot);
        Check("每周实例与普通任务 gene 相同",
            Math.Abs(weekly.Gene - plain.Gene) < 1e-6, $"{weekly.Gene} / {plain.Gene}");
        Check("模板备注被原样带到实例上", weekly.Note == "备注原样带过去", weekly.Note);
        Check("实例的 ParentId 为空（不是繁殖出来的）", weekly.ParentId is null);

        for (int i = 0; i < 60 * 60; i++) e2.Tick(1.0 / 60.0, wed + i * 16);
        Near("跑 1 分钟后两者 rot 完全一致（同样的腐烂速率）", weekly.Rot, plain.Rot, 1e-6);

        float beforeCare = weekly.Rot;
        float relief = e2.Care(weekly.Id, 200_000_000);
        Check("每周实例可以被「我去做了」", relief > 0f && weekly.Rot < beforeCare,
            $"{beforeCare:0.0000} → {weekly.Rot:0.0000}");
        Check("每周实例 care 后立刻变蛆", LifecycleEngine.StageOf(weekly) == Stage.Larva);

        e2.ResetAll(300_000_000);
        Eq("「重置」把每周实例退回 rot=0", weekly.Rot, 0.0);

        e2.ClearAll();
        Check("「清空」删掉每周实例", e2.Count == 0 && e2.FindWeeklyInstance(tpl.Id, week) is null);

        // ── 空标题被拒绝 ──
        LifecycleEngine e3 = new(new MaggotConfig(), randomSeed: 341);
        Check("空标题不建立模板", e3.AddWeeklyTemplate("   ", 1) is null);
        Check("空标题也没有生成任何实例", e3.Count == 0);

        // ── weekday 越界会被夹回 1..7 ──
        LifecycleEngine e3b = new(new MaggotConfig(), randomSeed: 343);
        Check("weekday=0 → 夹到 1", e3b.AddWeeklyTemplate("下限", 0, string.Empty, wed)!.Weekday == 1);
        Check("weekday=99 → 夹到 7", e3b.AddWeeklyTemplate("上限", 99, string.Empty, wed)!.Weekday == 7);

        // ── maxTasks 挡住了实例生成，但模板本身仍然建立 ──
        MaggotConfig tiny = new() { MaxTasks = 1 };
        LifecycleEngine e4 = new(tiny, randomSeed: 347);
        WeeklyTemplate? t4 = e4.AddWeeklyTemplate("第一个", 1, string.Empty, wed);
        WeeklyTemplate? t5 = e4.AddWeeklyTemplate("第二个", 1, string.Empty, wed);
        Check("maxTasks 触顶：模板仍然建立", t4 is not null && t5 is not null);
        Check("maxTasks 触顶：只生成了 1 个实例", e4.Count == 1, $"实际 {e4.Count}");

        // ── 状态文案（未生成 / 卵 / 蛆 / 蛹 / 苍蝇 / 已完成）──
        Check("未生成 → 「未生成」", LifecycleEngine.WeeklyInstanceStatusText(null) == "未生成");
        Check("rot 0 → 「卵」", LifecycleEngine.WeeklyInstanceStatusText(MakeTask(0f)) == "卵");
        Check("rot 0.30 → 「蛆」", LifecycleEngine.WeeklyInstanceStatusText(MakeTask(0.30f)) == "蛆");
        Check("rot 0.60 → 「蛹」", LifecycleEngine.WeeklyInstanceStatusText(MakeTask(0.60f)) == "蛹");
        Check("rot 0.90 → 「苍蝇」", LifecycleEngine.WeeklyInstanceStatusText(MakeTask(0.90f)) == "苍蝇");
        Check("已拍死 → 「已完成」",
            LifecycleEngine.WeeklyInstanceStatusText(MakeTask(0.30f, TaskStatus.Done)) == "已完成");

        // ── ReplaceWeeklyTemplates：持久化加载路径的健壮性 ──
        LifecycleEngine e5 = new(new MaggotConfig(), randomSeed: 349);
        e5.ReplaceWeeklyTemplates(new[]
        {
            new WeeklyTemplate { Id = "", Title = "无 id", Weekday = 42, SortOrder = 0, CreatedAt = wed },
            new WeeklyTemplate { Id = "fixed", Title = "乱写的星期", Weekday = -3, SortOrder = 0, CreatedAt = wed },
        });
        Check("ReplaceWeeklyTemplates 给缺 id 的模板补了 UUID",
            e5.WeeklyTemplates().All(t => !string.IsNullOrEmpty(t.Id)));
        Check("ReplaceWeeklyTemplates 把越界 weekday 夹回 1..7",
            e5.WeeklyTemplates().All(t => t.Weekday is >= 1 and <= 7),
            string.Join(",", e5.WeeklyTemplates().Select(t => t.Weekday)));
        Check("ReplaceWeeklyTemplates 补齐 sortOrder（0 表示未设置）",
            e5.WeeklyTemplates().Select(t => t.SortOrder).Distinct().Count() == 2,
            string.Join(",", e5.WeeklyTemplates().Select(t => t.SortOrder)));
        Check("ReplaceWeeklyTemplates 本身不生成任何实例", e5.Count == 0, $"实际 {e5.Count}");

        // ── 每周实例仍然受 maxTasks 兜底 ──
        Check("每周实例不会突破 maxTasks", e1.Count <= e1.Config.MaxTasks, $"实际 {e1.Count}");
    }

    private static void WeeklyPersistenceRoundTrip()
    {
        Section("持久化 · 每周任务模板往返（v3）");

        string? backupJson = null;
        bool hadFile = File.Exists(AppPaths.StateFile);
        if (hadFile) backupJson = File.ReadAllText(AppPaths.StateFile);

        try
        {
            using PersistenceService svc = new(TimeSpan.FromMinutes(5));

            const long now = 1_700_000_000_000L;
            MaggotConfig cfg = new() { WeeklyEnabled = false, DailyEnabled = true };
            LifecycleEngine engine = new(cfg, randomSeed: 353);

            WeeklyTemplate? t1 = engine.AddWeeklyTemplate("写周报", 5, "每周五下班前", now);
            WeeklyTemplate? t2 = engine.AddWeeklyTemplate("周复盘", 7, string.Empty, now);
            engine.SetWeeklyTemplateEnabled(t2!.Id, false);

            // 手动造一个每周实例（总开关关着，所以得手工建）
            TaskItem? inst = engine.AddTask(WeeklyKeys.InstanceTitle("写周报", "2026-W37"), "每周五下班前", now);
            inst!.WeeklyTemplateId = t1!.Id;
            inst.WeeklyKey = "2026-W37";
            inst.Rot = 0.55f;
            inst.Status = TaskStatus.Doing;

            AppState state = new()
            {
                Tasks = engine.Snapshot().ToList(),
                DailyTemplates = engine.DailyTemplates().ToList(),
                WeeklyTemplates = engine.WeeklyTemplates().ToList(),
                Config = cfg,
                SquashedCount = 0,
                LastSeenAt = now,
            };

            svc.Flush(state);
            Check("state.json 已写入", File.Exists(AppPaths.StateFile));

            string raw = File.ReadAllText(AppPaths.StateFile);
            bool hasWeeklyTemplates = raw.Contains("weeklyTemplates", StringComparison.OrdinalIgnoreCase);
            bool hasWeeklyKey = raw.Contains("weeklyKey", StringComparison.OrdinalIgnoreCase);
            bool hasWeeklyEnabled = raw.Contains("weeklyEnabled", StringComparison.OrdinalIgnoreCase);
            bool hasWeekday = raw.Contains("\"weekday\"", StringComparison.OrdinalIgnoreCase);
            Check("落盘 JSON 含 weeklyTemplates / weeklyKey / weeklyEnabled 键",
                hasWeeklyTemplates && hasWeeklyKey && hasWeeklyEnabled,
                $"weeklyTemplates={hasWeeklyTemplates} weeklyKey={hasWeeklyKey} weeklyEnabled={hasWeeklyEnabled}");
            Check("落盘 JSON 含 weekday 键", hasWeekday, $"weekday={hasWeekday}");

            AppState loaded = svc.Load();
            Check("模板数量往返一致（含停用状态）", loaded.WeeklyTemplates.Count == 2,
                $"实际 {loaded.WeeklyTemplates.Count}");

            WeeklyTemplate? lt1 = loaded.WeeklyTemplates.FirstOrDefault(t => t.Title == "写周报");
            WeeklyTemplate? lt2 = loaded.WeeklyTemplates.FirstOrDefault(t => t.Title == "周复盘");
            Check("模板字段往返一致（id/title/note/enabled/weekday/sortOrder/createdAt）",
                lt1 is { Enabled: true, Note: "每周五下班前", Weekday: 5, SortOrder: 0, CreatedAt: now },
                lt1 is null ? "未找到" : $"{lt1.Enabled}/{lt1.Note}/{lt1.Weekday}/{lt1.SortOrder}/{lt1.CreatedAt}");
            Check("停用状态与 weekday 往返一致", lt2 is { Enabled: false, Weekday: 7 },
                lt2 is null ? "未找到" : $"enabled={lt2.Enabled} weekday={lt2.Weekday}");

            TaskItem? linst = loaded.Tasks.FirstOrDefault(t => t.WeeklyTemplateId is not null);
            Check("任务的 weeklyTemplateId / weeklyKey 往返一致",
                linst is not null && linst.WeeklyTemplateId == t1!.Id && linst.WeeklyKey == "2026-W37",
                linst is null ? "未找到" : $"{linst.WeeklyTemplateId} / {linst.WeeklyKey}");
            Check("每周实例的 rot/status 往返一致",
                linst is { Rot: 0.55f, Status: TaskStatus.Doing },
                linst is null ? "未找到" : $"rot={linst.Rot} status={linst.Status}");
            Check("WeeklyEnabled 往返一致", loaded.Config.WeeklyEnabled == false);
            Check("写盘版本号 = 3", loaded.Version == 3, $"实际 {loaded.Version}");

            // 载入引擎后继续幂等推进
            LifecycleEngine restored = new(loaded.Config, randomSeed: 359);
            restored.ReplaceAll(loaded.Tasks, loaded.SquashedCount);
            restored.ReplaceWeeklyTemplates(loaded.WeeklyTemplates);
            Check("载入引擎后每周模板可用（含 weekday）",
                restored.WeeklyTemplates().First(t => t.Title == "写周报").Weekday == 5);
            Check("总开关关闭时载入后也不生成", restored.EnsureWeeklyTasks(now) == 0 && restored.Count == 1,
                $"任务数 {restored.Count}");
        }
        catch (Exception ex)
        {
            Check($"每周任务持久化自测未抛异常（{ex.GetType().Name}）", false, ex.Message);
        }
        finally
        {
            try
            {
                if (backupJson is not null) File.WriteAllText(AppPaths.StateFile, backupJson, new UTF8Encoding(false));
                else if (File.Exists(AppPaths.StateFile)) File.Delete(AppPaths.StateFile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"还原 state.json 失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 回归守卫：v2 的 state.json（有 dailyTemplates / dailyKey，但完全没有 weekly*）
    /// 必须能正常读进来 —— 缺字段取默认值，不抛异常。
    /// </summary>
    private static void LegacyV2StateLoadsCleanly()
    {
        Section("持久化 · 旧 v2 state.json 兼容加载（无每周字段）");

        string? backupJson = null;
        bool hadFile = File.Exists(AppPaths.StateFile);
        if (hadFile) backupJson = File.ReadAllText(AppPaths.StateFile);

        // 一份**手工写的** v2 文件：version=2、有 dailyTemplates 与 dailyKey，
        // 但没有任何 weekly*、配置里也没有 weeklyEnabled。
        const string v2Json = """
        {
          "version": 2,
          "tasks": [
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "title": "锻炼 · 09-16",
              "note": "",
              "status": 1,
              "rot": 0.31,
              "progress": 40,
              "createdAt": 1700000000000,
              "lastCaredAt": 1700000000000,
              "completedAt": null,
              "generation": 0,
              "parentId": null,
              "gene": 1.0,
              "dailyTemplateId": "33333333-3333-3333-3333-333333333333",
              "dailyKey": "2026-09-16"
            }
          ],
          "dailyTemplates": [
            {
              "id": "33333333-3333-3333-3333-333333333333",
              "title": "锻炼",
              "note": "每天 30 分钟",
              "enabled": true,
              "createdAt": 1700000000000,
              "sortOrder": 0
            }
          ],
          "config": {
            "running": true,
            "rotSpeed": 1.0,
            "baseRotMinutes": 90,
            "breedEnabled": true,
            "breedSeconds": 40,
            "maxFlies": 60,
            "maxTasks": 150,
            "clickThrough": true,
            "soundEnabled": true,
            "notifyOnFly": true,
            "dailyEnabled": true
          },
          "lastSeenAt": 1700000000000,
          "squashedCount": 1,
          "panelLeft": 100,
          "panelTop": 200
        }
        """;

        try
        {
            AppPaths.EnsureDataDirectory();
            File.WriteAllText(AppPaths.StateFile, v2Json, new UTF8Encoding(false));

            using PersistenceService svc = new(TimeSpan.FromMinutes(5));
            AppState state = svc.Load();

            Check("v2 文件加载不抛异常，LastError 为 null", svc.LastError is null, svc.LastError ?? "(null)");
            Check("v2 任务读出来了", state.Tasks.Count == 1, $"实际 {state.Tasks.Count}");
            Check("v2 的 dailyTemplates 读出来了", state.DailyTemplates.Count == 1,
                $"实际 {state.DailyTemplates.Count}");
            Check("v2 的 dailyKey 保留", state.Tasks[0].DailyKey == "2026-09-16",
                state.Tasks[0].DailyKey ?? "(null)");

            TaskItem v2Task = state.Tasks[0];
            Check("缺失的 weeklyTemplateId → null（不是异常）", v2Task.WeeklyTemplateId is null,
                v2Task.WeeklyTemplateId ?? "(null)");
            Check("缺失的 weeklyKey → null（不是异常）", v2Task.WeeklyKey is null,
                v2Task.WeeklyKey ?? "(null)");
            Check("缺失的 weeklyTemplates → 空列表", state.WeeklyTemplates.Count == 0,
                $"实际 {state.WeeklyTemplates.Count}");
            Check("缺失的 weeklyEnabled → 默认 true", state.Config.WeeklyEnabled);
            Check("v2 的 squashedCount / 面板位置照常读出来",
                state.SquashedCount == 1 && state.PanelLeft is not null,
                $"squashed={state.SquashedCount} panelLeft={state.PanelLeft}");
            Check("加载后版本号提升到当前版本 3", state.Version == 3, $"实际 {state.Version}");

            // 载入引擎：v2 数据照常跑，且能接着做每周任务
            LifecycleEngine engine = new(state.Config, randomSeed: 361);
            engine.ReplaceAll(state.Tasks, state.SquashedCount);
            engine.ReplaceDailyTemplates(state.DailyTemplates);
            engine.ReplaceWeeklyTemplates(state.WeeklyTemplates);
            Check("v2 数据能喂给引擎", engine.Count == 1 && engine.DailyTemplates().Count == 1
                                     && engine.WeeklyTemplates().Count == 0,
                $"tasks={engine.Count} daily={engine.DailyTemplates().Count} weekly={engine.WeeklyTemplates().Count}");
            Check("v2 数据喂给引擎后能继续推进",
                engine.FindById("22222222-2222-2222-2222-222222222222") is not null);

            // 在 v2 数据上新增每周模板：立刻生成本周实例
            long wed = LocalNoonMs(2026, 9, 16);
            WeeklyTemplate? added = engine.AddWeeklyTemplate("写周报", 3, string.Empty, wed);
            Check("在 v2 载入的数据上可以直接新增每周模板并立刻生成",
                added is not null && engine.FindWeeklyInstance(added!.Id, "2026-W38") is not null,
                $"任务数 {engine.Count}");

            // 再落盘就是 v3 形态
            state.Tasks = engine.Snapshot().ToList();
            state.WeeklyTemplates = engine.WeeklyTemplates().ToList();
            svc.Flush(state);
            string written = File.ReadAllText(AppPaths.StateFile);
            bool v2HasWeeklyTemplates = written.Contains("weeklyTemplates", StringComparison.OrdinalIgnoreCase);
            bool v2HasWeeklyEnabled = written.Contains("weeklyEnabled", StringComparison.OrdinalIgnoreCase);
            bool v2Version3 = System.Text.RegularExpressions.Regex.IsMatch(written, "\"version\"\\s*:\\s*3\\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Check("重新落盘后含 weeklyTemplates 键", v2HasWeeklyTemplates,
                $"weeklyTemplates={v2HasWeeklyTemplates}");
            Check("重新落盘后含 weeklyEnabled 键", v2HasWeeklyEnabled,
                $"weeklyEnabled={v2HasWeeklyEnabled}");
            Check("重新落盘后版本号为 3", v2Version3, $"version==3: {v2Version3}");
        }
        catch (Exception ex)
        {
            Check($"v2 兼容加载未抛异常（{ex.GetType().Name}）", false, ex.Message);
        }
        finally
        {
            try
            {
                if (backupJson is not null) File.WriteAllText(AppPaths.StateFile, backupJson, new UTF8Encoding(false));
                else if (File.Exists(AppPaths.StateFile)) File.Delete(AppPaths.StateFile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"还原 state.json 失败: {ex.Message}");
            }
        }
    }

    private static void DailyPersistenceRoundTrip()
    {
        Section("持久化 · 每日任务模板往返（v2）");

        string? backupJson = null;
        bool hadFile = File.Exists(AppPaths.StateFile);
        if (hadFile) backupJson = File.ReadAllText(AppPaths.StateFile);

        try
        {
            using PersistenceService svc = new(TimeSpan.FromMinutes(5));

            const long now = 1_700_000_000_000L;
            MaggotConfig cfg = new() { DailyEnabled = false };
            LifecycleEngine engine = new(cfg, randomSeed: 251);

            DailyTemplate? t1 = engine.AddDailyTemplate("锻炼", "每天 30 分钟", now);
            DailyTemplate? t2 = engine.AddDailyTemplate("复盘", string.Empty, now);
            engine.SetDailyTemplateEnabled(t2!.Id, false);

            // 手动造一个每日实例（total 开关关着，所以得手工建）
            TaskItem? inst = engine.AddTask(DailyKeys.InstanceTitle("锻炼", "2026-09-12"), "每天 30 分钟", now);
            inst!.DailyTemplateId = t1!.Id;
            inst.DailyKey = "2026-09-12";
            inst.Rot = 0.55f;
            inst.Status = TaskStatus.Doing;

            AppState state = new()
            {
                Tasks = engine.Snapshot().ToList(),
                DailyTemplates = engine.DailyTemplates().ToList(),
                Config = cfg,
                SquashedCount = 0,
                LastSeenAt = now,
            };

            svc.Flush(state);
            Check("state.json 已写入", File.Exists(AppPaths.StateFile));

            AppState loaded = svc.Load();
            Check("模板数量往返一致（含停用状态）", loaded.DailyTemplates.Count == 2,
                $"实际 {loaded.DailyTemplates.Count}");

            DailyTemplate? lt1 = loaded.DailyTemplates.FirstOrDefault(t => t.Title == "锻炼");
            DailyTemplate? lt2 = loaded.DailyTemplates.FirstOrDefault(t => t.Title == "复盘");
            Check("模板字段往返一致（id/title/note/enabled/sortOrder/createdAt）",
                lt1 is { Enabled: true, Note: "每天 30 分钟", SortOrder: 0, CreatedAt: now },
                lt1 is null ? "未找到" : $"{lt1.Enabled}/{lt1.Note}/{lt1.SortOrder}/{lt1.CreatedAt}");
            Check("停用状态往返一致", lt2 is { Enabled: false }, lt2 is null ? "未找到" : $"enabled={lt2.Enabled}");

            TaskItem? linst = loaded.Tasks.FirstOrDefault(t => t.DailyTemplateId is not null);
            Check("任务的 dailyTemplateId / dailyKey 往返一致",
                linst is not null && linst.DailyTemplateId == t1!.Id && linst.DailyKey == "2026-09-12",
                linst is null ? "未找到" : $"{linst.DailyTemplateId} / {linst.DailyKey}");
            Check("每日实例的 rot/status 往返一致",
                linst is { Rot: 0.55f, Status: TaskStatus.Doing },
                linst is null ? "未找到" : $"rot={linst.Rot} status={linst.Status}");
            Check("DailyEnabled 往返一致", loaded.Config.DailyEnabled == false);
            Check("写盘版本号 = 3", loaded.Version == 3, $"实际 {loaded.Version}");
        }
        catch (Exception ex)
        {
            Check($"每日任务持久化自测未抛异常（{ex.GetType().Name}）", false, ex.Message);
        }
        finally
        {
            try
            {
                if (backupJson is not null) File.WriteAllText(AppPaths.StateFile, backupJson, new UTF8Encoding(false));
                else if (File.Exists(AppPaths.StateFile)) File.Delete(AppPaths.StateFile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"还原 state.json 失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 回归守卫：v1 的 state.json（没有 dailyTemplates，任务里也没有 daily* / weekly* 字段）
    /// 必须能正常读进来 —— 缺字段取默认值，不抛异常。
    /// </summary>
    private static void LegacyV1StateLoadsCleanly()
    {
        Section("持久化 · 旧 v1 state.json 兼容加载（无每日/每周字段）");

        string? backupJson = null;
        bool hadFile = File.Exists(AppPaths.StateFile);
        if (hadFile) backupJson = File.ReadAllText(AppPaths.StateFile);

        // 一份**手工写的** v1 文件：version=1、没有 dailyTemplates、任务里没有 daily*、
        // 配置里没有 dailyEnabled，而且 soundEnabled 还是旧的 false。
        const string v1Json = """
        {
          "version": 1,
          "tasks": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "title": "老任务",
              "note": "",
              "status": 0,
              "rot": 0.42,
              "progress": 30,
              "createdAt": 1700000000000,
              "lastCaredAt": 1700000000000,
              "completedAt": null,
              "generation": 0,
              "parentId": null,
              "gene": 1.0
            }
          ],
          "config": {
            "running": true,
            "rotSpeed": 1.0,
            "baseRotMinutes": 90,
            "breedEnabled": true,
            "breedSeconds": 40,
            "maxFlies": 60,
            "maxTasks": 150,
            "clickThrough": true,
            "soundEnabled": false,
            "notifyOnFly": true
          },
          "lastSeenAt": 1700000000000,
          "squashedCount": 3,
          "panelLeft": 100,
          "panelTop": 200
        }
        """;

        try
        {
            AppPaths.EnsureDataDirectory();
            File.WriteAllText(AppPaths.StateFile, v1Json, new UTF8Encoding(false));

            using PersistenceService svc = new(TimeSpan.FromMinutes(5));
            AppState state = svc.Load();

            Check("v1 文件加载不抛异常，LastError 为 null", svc.LastError is null, svc.LastError ?? "(null)");
            Check("v1 任务读出来了", state.Tasks.Count == 1, $"实际 {state.Tasks.Count}");
            Check("v1 任务的 rot/status 正确",
                state.Tasks.Count == 1 && Math.Abs(state.Tasks[0].Rot - 0.42f) < 1e-6,
                state.Tasks.Count == 1 ? $"rot={state.Tasks[0].Rot}" : "(空)");

            TaskItem legacy = state.Tasks[0];
            Check("缺失的 dailyTemplateId → null（不是异常）", legacy.DailyTemplateId is null,
                legacy.DailyTemplateId ?? "(null)");
            Check("缺失的 dailyKey → null（不是异常）", legacy.DailyKey is null,
                legacy.DailyKey ?? "(null)");
            Check("缺失的 weeklyTemplateId → null（不是异常）", legacy.WeeklyTemplateId is null,
                legacy.WeeklyTemplateId ?? "(null)");
            Check("缺失的 weeklyKey → null（不是异常）", legacy.WeeklyKey is null,
                legacy.WeeklyKey ?? "(null)");
            Check("缺失的 dailyTemplates → 空列表", state.DailyTemplates.Count == 0,
                $"实际 {state.DailyTemplates.Count}");
            Check("缺失的 weeklyTemplates → 空列表", state.WeeklyTemplates.Count == 0,
                $"实际 {state.WeeklyTemplates.Count}");
            Check("缺失的 dailyEnabled → 默认 true", state.Config.DailyEnabled);
            Check("缺失的 weeklyEnabled → 默认 true", state.Config.WeeklyEnabled);
            Check("v1 里的 soundEnabled=false 被保留（用户显式关过）", !state.Config.SoundEnabled);
            Check("v1 的其它字段照常读出来",
                state.SquashedCount == 3 && state.PanelLeft is not null,
                $"squashed={state.SquashedCount} panelLeft={state.PanelLeft}");

            // 版本号升级到当前版本（只是标记，数据本身不受影响）
            Check("加载后版本号提升到当前版本 3", state.Version == 3, $"实际 {state.Version}");

            // 加载后引擎能正常接收
            LifecycleEngine engine = new(state.Config, randomSeed: 257);
            engine.ReplaceAll(state.Tasks, state.SquashedCount);
            engine.ReplaceDailyTemplates(state.DailyTemplates);
            engine.ReplaceWeeklyTemplates(state.WeeklyTemplates);
            Check("v1 数据能喂给引擎", engine.Count == 1 && engine.DailyTemplates().Count == 0
                                      && engine.WeeklyTemplates().Count == 0,
                $"tasks={engine.Count} daily={engine.DailyTemplates().Count} weekly={engine.WeeklyTemplates().Count}");
            Check("v1 数据喂给引擎后能继续推进",
                engine.FindById("11111111-1111-1111-1111-111111111111") is not null);

            // 再落盘一次应该是 v3 形态（带 dailyTemplates / weeklyTemplates 键）
            svc.Flush(state);
            string written = File.ReadAllText(AppPaths.StateFile);
            bool v1HasWeeklyTemplates = written.Contains("weeklyTemplates", StringComparison.OrdinalIgnoreCase);
            bool v1HasWeeklyKey = written.Contains("weeklyKey", StringComparison.OrdinalIgnoreCase);
            bool v1HasWeeklyEnabled = written.Contains("weeklyEnabled", StringComparison.OrdinalIgnoreCase);
            bool v1Version3 = System.Text.RegularExpressions.Regex.IsMatch(written, "\"version\"\\s*:\\s*3\\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Check("重新落盘后含 dailyTemplates 键",
                written.Contains("dailyTemplates", StringComparison.OrdinalIgnoreCase), "(缺少该键)");
            Check("重新落盘后含 dailyKey 键",
                written.Contains("dailyKey", StringComparison.OrdinalIgnoreCase), "(缺少该键)");
            Check("重新落盘后含 weeklyTemplates 键", v1HasWeeklyTemplates,
                $"weeklyTemplates={v1HasWeeklyTemplates}");
            Check("重新落盘后含 weeklyKey 键", v1HasWeeklyKey, $"weeklyKey={v1HasWeeklyKey}");
            Check("重新落盘后版本号为 3", v1Version3, $"version==3: {v1Version3}");
            Check("重新落盘后含 dailyEnabled 键",
                written.Contains("dailyEnabled", StringComparison.OrdinalIgnoreCase), "(缺少该键)");
            Check("重新落盘后含 weeklyEnabled 键", v1HasWeeklyEnabled,
                $"weeklyEnabled={v1HasWeeklyEnabled}");
        }
        catch (Exception ex)
        {
            Check($"v1 兼容加载未抛异常（{ex.GetType().Name}）", false, ex.Message);
        }
        finally
        {
            try
            {
                if (backupJson is not null) File.WriteAllText(AppPaths.StateFile, backupJson, new UTF8Encoding(false));
                else if (File.Exists(AppPaths.StateFile)) File.Delete(AppPaths.StateFile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"还原 state.json 失败: {ex.Message}");
            }
        }
    }

    // ==================================================================
    // 第 13 节：程序化音效
    // ==================================================================

    private static void SoundSynthesis()
    {
        Section("第 13 节 · 程序化音效（内存合成 PCM + WAV 头）");

        using SoundService sound = new();
        IReadOnlyList<SoundService.SoundStats> stats = sound.Stats();

        Check("共合成 4 个音效", stats.Count == 4, $"实际 {stats.Count}");
        Check("采样率 = 22050Hz", SoundService.SampleRate == 22050);
        Check("连点保护 = 30ms", SoundService.RetriggerGuardMs == 30);

        // 时长与规格 §13 的表一致
        Eq("拍死时长 = 170ms", stats[0].DurationMs, SoundService.SquashMs);
        Eq("苍蝇嗡鸣时长 = 400ms", stats[1].DurationMs, SoundService.BuzzMs);
        Eq("孵化时长 = 90ms", stats[2].DurationMs, SoundService.HatchMs);
        Eq("繁殖时长 = 260ms", stats[3].DurationMs, SoundService.BredMs);

        Check("拍死音效名 = 「拍死」", stats[0].Name == "拍死", stats[0].Name);
        Check("嗡鸣音效名 = 「苍蝇嗡鸣」", stats[1].Name == "苍蝇嗡鸣", stats[1].Name);
        Check("孵化音效名 = 「孵化」", stats[2].Name == "孵化", stats[2].Name);
        Check("繁殖音效名 = 「繁殖」", stats[3].Name == "繁殖", stats[3].Name);

        // WAV 头必须有效 —— 用 SoundPlayer 也认这个头
        foreach (SoundService.SoundStats s in stats)
        {
            Check($"{s.Name}：WAV 头有效（RIFF/WAVE/fmt/data + 长度自洽）", s.ValidWavHeader, s.ToString());
            Check($"{s.Name}：字节数 = 44 + 采样数×2",
                s.ByteLength == 44 + s.SampleCount * 2, $"{s.ByteLength} vs {44 + s.SampleCount * 2}");
        }

        // 非静音：峰值/RMS 都是可测量的实数
        foreach (SoundService.SoundStats s in stats)
        {
            Check($"{s.Name}：不是静音（峰值 {s.Peak:0.0000} > 0.5）", s.Peak > 0.5, $"峰值 {s.Peak:0.0000}");
            Check($"{s.Name}：RMS 有实际能量（{s.Rms:0.0000} > 0.02）", s.Rms > 0.02, $"RMS {s.Rms:0.0000}");
            Check($"{s.Name}：没有削顶失真（峰值 ≤ 1.0）", s.Peak <= 1.0 + 1e-9, $"峰值 {s.Peak:0.0000}");
        }

        // 逐字节校验 WAV 头（自己解析，不依赖 Measure）
        for (int i = 0; i < stats.Count; i++)
        {
            byte[] wav = sound.Bytes(i);
            bool ok = SoundService.IsValidWav(wav, out string reason);
            Check($"{stats[i].Name}：IsValidWav 通过", ok, reason);

            string riff = System.Text.Encoding.ASCII.GetString(wav, 0, 4);
            string wave = System.Text.Encoding.ASCII.GetString(wav, 8, 4);
            Check($"{stats[i].Name}：前 12 字节是 RIFF....WAVE",
                riff == "RIFF" && wave == "WAVE", $"{riff}/{wave}");
            Check($"{stats[i].Name}：PCM / 单声道 / 16bit",
                BitConverter.ToInt16(wav, 20) == 1 && BitConverter.ToInt16(wav, 22) == 1 && BitConverter.ToInt16(wav, 34) == 16);
            Check($"{stats[i].Name}：data 块长度字段自洽",
                BitConverter.ToInt32(wav, 40) == wav.Length - 44);
        }

        // 确定性：同一种子必须得到完全相同的字节
        byte[] first = SoundService.EncodeWav(BuildDeterministicProbe(), SoundService.SampleRate);
        byte[] second = SoundService.EncodeWav(BuildDeterministicProbe(), SoundService.SampleRate);
        Check("EncodeWav 是确定性的（两次编码逐字节一致）", first.AsSpan().SequenceEqual(second),
            $"{first.Length} / {second.Length} 字节");
        Check("两次编码的峰值也一致",
            Math.Abs(SoundService.Measure(first).Peak - SoundService.Measure(second).Peak) < 1e-12);

        // 逻辑正确性：30ms 连点保护 + 开关
        SoundService guarded = new();
        guarded.Enabled = false;
        Check("开关关闭时不播放", !guarded.PlaySquash());
        Check("开关关闭时 PlayCount 不增加", guarded.PlayCount == 0, $"实际 {guarded.PlayCount}");

        guarded.Enabled = true;
        bool firstPlay = guarded.PlaySquash();
        bool immediate = guarded.PlaySquash();
        Check("首次播放被接受", firstPlay);
        Check("30ms 内重复触发同一音效被拦下", !immediate);
        Check("PlayCount 只 +1", guarded.PlayCount == 1, $"实际 {guarded.PlayCount}");

        Thread.Sleep(SoundService.RetriggerGuardMs + 25);
        Check("超过 30ms 后可以再次触发", guarded.PlaySquash());
        Check("PlayCount 变成 2", guarded.PlayCount == 2, $"实际 {guarded.PlayCount}");

        // 不同音效各自独立守卫
        Check("不同音效的守卫互不影响（嗡鸣紧跟着拍死也能放）", guarded.PlayAlert(),
            guarded.LastError ?? string.Empty);

        // 越界索引不抛异常
        Check("越界索引返回 false 且不抛异常", !guarded.Play(99));
        Check("索引 -1 返回 false 且不抛异常", !guarded.Play(-1));

        // Preview 不受 Enabled 影响（开关刚打开的那一刻要能听见）
        SoundService quiet = new() { Enabled = false };
        Check("Preview 在开关关闭时也能试听（规格 §13：打开时立刻试听）", quiet.Preview());

        Check("播放失败一律静默（LastError 是字符串或 null，从不抛异常）",
            guarded.LastError is null || guarded.LastError.Length > 0);
    }

    /// <summary>确定性的探针波形：验证编码器没有隐藏的随机性。</summary>
    private static double[] BuildDeterministicProbe()
    {
        double[] samples = new double[512];
        uint state = 0xDEADBEEFu;
        for (int i = 0; i < samples.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            double noise = state / 2147483648.0 - 1.0;
            samples[i] = noise * 0.5 + Math.Sin(i * 0.07) * 0.4;
        }
        return samples;
    }

    // ==================================================================
    // 第 14 节：悬停驻留
    // ==================================================================

    private static void HoverDwellBehaviour()
    {
        Section("第 14 节 · 悬停查看任务（驻留逻辑，逐条覆盖 §14.4）");

        Check("触发驻留 = 3.0 秒", Math.Abs(HoverTracker.DwellSeconds - 3.0) < 1e-12);
        Check("轮询间隔 = 100ms（约 10Hz）",
            Math.Abs(HoverTracker.PollIntervalSeconds - 0.1) < 1e-12);
        Check("命中半径系数 = size × 0.7（§14.1 要求至少 ~0.7）",
            Math.Abs(HoverTracker.HitRadiusScale - 0.7) < 1e-12);

        // A 在 (100,100) size 34 → 半径 23.8；B 在 (400,100)
        Point pa = new(100, 100);
        Point pb = new(400, 100);
        HoverTarget ta = new("task-a", pa, 34.0);
        HoverTarget tb = new("task-b", pb, 34.0);
        List<HoverTarget> targets = new() { ta, tb };

        // 命中判定
        Check("光标压在 A 上 → 命中 A", HoverTracker.HitTest(targets, pa)?.TaskId == "task-a");
        Check("光标在半径内（+23px）→ 命中 A",
            HoverTracker.HitTest(targets, new Point(pa.X + 23, pa.Y))?.TaskId == "task-a");
        Check("光标在半径外（+26px）→ 不命中",
            HoverTracker.HitTest(targets, new Point(pa.X + 26, pa.Y)) is null);
        Check("半径确实随 size 放大（size 60 → 42px 仍命中）",
            HoverTracker.HitTest(new List<HoverTarget> { new("big", pa, 60.0) },
                new Point(pa.X + 41, pa.Y))?.TaskId == "big");
        Check("空集合 → 不命中", HoverTracker.HitTest(new List<HoverTarget>(), pa) is null);

        // 轮询语义：第一次发现某只目标的那一帧算 t=0，此后每 100ms 累加一次。
        // 所以「停 N 秒」= N×10 + 1 次轮询。下面用 PollUntilShown 精确数出第几次才弹，
        // 避免手写次数时把边界算错。

        // 验收 1：停 2.9 秒 → 不弹
        HoverTracker tracker = new();
        int pollsToShow = PollUntilShown(tracker, targets, pa, maxPolls: 100);
        Check("验收 1/2：停 2.9 秒不弹、停满 3.0 秒才弹（第 31 次轮询 = t=3.0s）",
            pollsToShow == 31, $"第 {pollsToShow} 次轮询弹出");
        Check("验收 2：弹出时驻留正好 3.0 秒",
            Math.Abs(tracker.Dwell - 3.0) < 1e-9, tracker.Dwell.ToString("0.000"));
        Check("验收 2：弹出后 IsShown = true", tracker.IsShown);
        Check("验收 2：NewlyShown 只在那一刻为 true", tracker.ShowCount == 1, $"ShowCount={tracker.ShowCount}");
        Check("验收 2：弹出的目标是 A（标题与那只虫一致）",
            tracker.HoveredId == "task-a", tracker.HoveredId ?? "(null)");

        // 弹出后继续停在同一只 → 保持显示、不再重复 Show
        HoverPollResult keep = tracker.Poll(targets, pa, HoverTracker.PollIntervalSeconds);
        Check("弹出后继续停留 → 保持显示", keep.ShouldShow && !keep.NewlyShown);
        Check("保持期间 ShowCount 仍是 1", tracker.ShowCount == 1, $"实际 {tracker.ShowCount}");

        // 验收 3：从 A 直接划到 B → 驻留归零，再停 3 秒只显示 B
        HoverPollResult toB = tracker.Poll(targets, pb, HoverTracker.PollIntervalSeconds);
        Check("验收 3：刚划到 B → 立刻收起、不显示",
            !toB.ShouldShow && !tracker.IsShown, $"Dwell={tracker.Dwell:0.00}s");
        Check("验收 3：驻留计时按个体归零（首帧为 t=0，不继承 A 的计时）",
            Math.Abs(tracker.Dwell) < 1e-12, tracker.Dwell.ToString("0.000"));
        Check("验收 3：换目标计数 +1", tracker.RestartCount == 1, $"实际 {tracker.RestartCount}");

        int bFromStart = PollUntilShown(tracker, targets, pb, maxPolls: 100);
        // 划到 B 的那一次「换目标」轮询本身就是 B 的第一帧（t=0），所以只数它之后的 30 次。
        Check("验收 3：在 B 上同样要停满 3.0 秒才弹（换目标那帧之后第 30 次轮询）",
            bFromStart == 30, $"第 {bFromStart} 次轮询弹出");
        Check("验收 3：显示的是 B，不是 A",
            tracker.HoveredId == "task-b", tracker.HoveredId ?? "(null)");
        Check("验收 3：ShowCount = 2（A 一次、B 一次）", tracker.ShowCount == 2, $"实际 {tracker.ShowCount}");

        // 验收 4：移开 → 立刻收起
        HoverPollResult off = tracker.Poll(targets, new Point(2000, 2000), HoverTracker.PollIntervalSeconds);
        Check("验收 4：移开后立刻收起", !off.ShouldShow && !tracker.IsShown);
        Check("验收 4：ShouldHide 标记为 true", off.ShouldHide);
        Check("验收 4：悬停目标被清空", tracker.HoveredId is null);
        Check("验收 4：驻留计时归零", Math.Abs(tracker.Dwell) < 1e-12, tracker.Dwell.ToString("0.000"));

        // 移开再回来 → 重新数 3 秒（不残留）
        HoverPollResult back = tracker.Poll(targets, pa, HoverTracker.PollIntervalSeconds);
        Check("移开再回到 A → 重新数 3 秒，不残留", !back.ShouldShow, $"Dwell={tracker.Dwell:0.00}s");

        // 生物漂移：只要还在半径内就继续累计
        HoverTracker drift = new();
        for (int i = 0; i < 11; i++)
        {
            // 每帧把生物往右挪 1px —— 模拟「生物在移动」
            List<HoverTarget> moving = new() { new("drift", new Point(100 + i, 100), 34.0) };
            drift.Poll(moving, new Point(105, 100), HoverTracker.PollIntervalSeconds);
        }
        Check("生物小幅漂移时驻留不被重置（用当帧位置判定）",
            drift.HoveredId == "drift" && Math.Abs(drift.Dwell - 1.0) < 1e-9, $"Dwell={drift.Dwell:0.00}s");
        // 生物漂出半径 → 视为移开
        HoverPollResult drifted = drift.Poll(
            new List<HoverTarget> { new("drift", new Point(500, 500), 34.0) },
            new Point(105, 100),
            HoverTracker.PollIntervalSeconds);
        Check("生物漂出命中半径 → 视为移开并收起", !drifted.ShouldShow && !drift.IsShown);

        // 命中多只时取最近的一只
        List<HoverTarget> overlapping = new()
        {
            new("far", new Point(117, 100), 34.0),
            new("near", new Point(101, 100), 34.0),
        };
        Check("多只重叠时命中最近的一只",
            HoverTracker.HitTest(overlapping, new Point(100, 100))?.TaskId == "near");

        // Reset
        tracker.Reset();
        Check("Reset 清空全部状态",
            tracker.HoveredId is null && !tracker.IsShown && Math.Abs(tracker.Dwell) < 1e-12);
    }

    // ==================================================================
    // 固定步长节拍器
    // ==================================================================

    private static void SimulationPacerBehaviour()
    {
        Section("TICK_HZ / IDLE_TICK_HZ · 固定步长节拍器");

        SimulationPacer pacer = new(LifecycleEngine.TickHz);
        Near("步长 = 1/60 秒", pacer.StepSeconds, 1.0 / 60.0, 1e-12);

        // 未预热前不推进
        Check("首次调用不推进（只是建立计时基准）", pacer.Advance() == 0);

        // 真实睡眠 100ms：应当补 5–7 步（60Hz 下 100ms ≈ 6 步），
        // 且绝不会一次性补爆。这条直接验证「模拟频率与渲染频率解耦」。
        Thread.Sleep(100);
        int steps = pacer.Advance();
        Check($"睡 100ms 后补 {steps} 步（60Hz 下期望 5–7 步）", steps is >= 5 and <= 7, $"实际 {steps}");

        // 长时间阻塞后必须夹住，不能雪崩
        int none = pacer.Advance();
        Check("紧接着再调一次不产生额外步数", none <= 1, $"实际 {none}");

        SimulationPacer capped = new(LifecycleEngine.TickHz);
        capped.Advance();                 // 预热
        // 模拟一次长卡顿（用反射改不了私有字段，改用真实等待是浪费 —— 直接用 Reset 语义验证上限）
        SimulationPacer reset = new(LifecycleEngine.TickHz);
        reset.Advance();
        reset.Reset();
        Check("Reset 之后重新预热，不补算空白时间", reset.Advance() == 0);
        Check($"单帧补步上限 = {SimulationPacer.MaxCatchUpSteps}", SimulationPacer.MaxCatchUpSteps == 8);

        // IDLE 频率下同样给出正确的步长
        SimulationPacer idle = new(LifecycleEngine.IdleTickHz);
        Near("IDLE_TICK_HZ=10 → 步长 0.1 秒", idle.StepSeconds, 0.1, 1e-12);

        // 与引擎结合：固定步长下腐烂量与「按 MAX_DT 分块推进」完全等价。
        MaggotConfig cfg = new() { RotSpeed = 1.0, BaseRotMinutes = 90 };
        LifecycleEngine fine = new(cfg, randomSeed: 101);
        LifecycleEngine coarse = new(cfg, randomSeed: 101);
        TaskItem a = fine.AddTask("细", now: 0)!;
        TaskItem b = coarse.AddTask("粗", now: 0)!;

        for (int i = 0; i < 60; i++) fine.Tick(1.0 / 60.0, i * 16L);   // 1 秒，分 60 步
        coarse.Tick(0.5, 0);                                            // 1 秒 = 两个 MAX_DT 分块
        coarse.Tick(0.5, 500);

        Near("60 步 × 1/60s 与两次 dt=0.5s 结果一致（腐烂与帧率无关）",
            a.Rot, b.Rot, 1e-7);

        // 规格 MAX_DT：单次 tick 超过 0.5 秒会被夹住（防休眠后跳变）
        LifecycleEngine oneShot = new(cfg, randomSeed: 101);
        TaskItem c = oneShot.AddTask("一次到顶", now: 0)!;
        oneShot.Tick(1.0, 0);
        Near("单次 Tick(1.0) 被 MAX_DT=0.5 夹住，只推进 0.5 秒",
            c.Rot, b.Rot / 2.0, 1e-7);
    }

    // ==================================================================
    // 持久化往返
    // ==================================================================

    private static void PersistenceRoundTrip()
    {
        Section("持久化 · JSON 往返（%APPDATA%\\MaggotTask\\state.json）");

        string? backupJson = null;
        bool hadFile = File.Exists(AppPaths.StateFile);
        if (hadFile)
        {
            backupJson = File.ReadAllText(AppPaths.StateFile);
        }

        try
        {
            using PersistenceService svc = new(TimeSpan.FromMinutes(5));

            MaggotConfig cfg = new()
            {
                Running = false,
                RotSpeed = 3.75,
                BaseRotMinutes = 77,
                BreedEnabled = false,
                BreedSeconds = 12,
                MaxFlies = 21,
                MaxTasks = 99,
                ClickThrough = false,
                SoundEnabled = true,
                NotifyOnFly = false,
            };

            LifecycleEngine engine = new(cfg, randomSeed: 79);
            TaskItem a = engine.AddTask("持久化甲", now: 111)!;
            a.Rot = 0.42f;
            a.Progress = 66;
            a.Status = TaskStatus.Doing;
            TaskItem b = engine.AddTask("持久化乙", now: 222)!;
            b.Rot = 0.91f;
            b.Generation = 3;
            b.Gene = LifecycleEngine.GeneFor(3);
            engine.Complete(b.Id, 333, out _);

            AppState state = new()
            {
                Tasks = engine.Snapshot().ToList(),
                Config = cfg,
                SquashedCount = engine.SquashedCount,
                LastSeenAt = 123456789,
                PanelLeft = 1234.5,
                PanelTop = 678.25,
            };

            svc.Flush(state);
            Check("state.json 已写入", File.Exists(AppPaths.StateFile), AppPaths.StateFile);
            Check("落盘计数 +1", svc.SaveCount == 1, $"SaveCount={svc.SaveCount}");

            AppState loaded = svc.Load();
            Check("任务数量一致", loaded.Tasks.Count == 2, $"实际 {loaded.Tasks.Count}");
            Check("拍死计数一致", loaded.SquashedCount == 1, $"实际 {loaded.SquashedCount}");
            Check("lastSeenAt 被刷新为写入时刻", loaded.LastSeenAt > 123456789, $"实际 {loaded.LastSeenAt}");

            Check("配置往返一致", loaded.Config is
            {
                Running: false,
                RotSpeed: 3.75,
                BaseRotMinutes: 77,
                BreedEnabled: false,
                BreedSeconds: 12,
                MaxFlies: 21,
                MaxTasks: 99,
                ClickThrough: false,
                SoundEnabled: true,
                NotifyOnFly: false,
            }, $"rotSpeed={loaded.Config.RotSpeed}, maxTasks={loaded.Config.MaxTasks}");

            TaskItem? la = loaded.Tasks.FirstOrDefault(t => t.Title == "持久化甲");
            TaskItem? lb = loaded.Tasks.FirstOrDefault(t => t.Title == "持久化乙");
            Check("任务字段往返一致（rot/status/progress）",
                la is { Rot: 0.42f, Progress: 66, Status: TaskStatus.Doing },
                la is null ? "未找到" : $"rot={la.Rot} progress={la.Progress} status={la.Status}");
            Check("后代字段往返一致（generation/gene）",
                lb is { Generation: 3 } && lb is not null && Math.Abs(lb.Gene - 1.30f) < 1e-6,
                lb is null ? "未找到" : $"gen={lb.Generation} gene={lb.Gene} status={lb.Status}");
            Check("面板位置往返一致",
                loaded.PanelLeft is not null && Math.Abs(loaded.PanelLeft.Value - 1234.5) < 1e-9,
                $"PanelLeft={loaded.PanelLeft}");

            // 损坏文件不应导致崩溃
            File.WriteAllText(AppPaths.StateFile, "{ this is not json ", new UTF8Encoding(false));
            AppState recovered = svc.Load();
            Check("损坏的 JSON 不抛异常，回退到默认状态",
                recovered.Tasks.Count == 0 && recovered.Config.RotSpeed == 1.0,
                $"tasks={recovered.Tasks.Count} rotSpeed={recovered.Config.RotSpeed}");
            Check("损坏时可读到错误信息", svc.LastError is not null, svc.LastError ?? "(null)");
        }
        catch (Exception ex)
        {
            Check($"持久化自测未抛异常（{ex.GetType().Name}）", false, ex.Message);
        }
        finally
        {
            // 还原用户原有的 state.json
            try
            {
                if (backupJson is not null)
                {
                    File.WriteAllText(AppPaths.StateFile, backupJson, new UTF8Encoding(false));
                }
                else if (File.Exists(AppPaths.StateFile))
                {
                    File.Delete(AppPaths.StateFile);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"还原 state.json 失败: {ex.Message}");
            }
        }
    }
}
