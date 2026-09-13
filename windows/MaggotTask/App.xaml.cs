using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaggotTask.Core;
using MaggotTask.Render;
using MaggotTask.Services;
using MaggotTask.View;

namespace MaggotTask;

/// <summary>
/// 应用入口与总装配。
///
/// 主循环：<see cref="CompositionTarget.Rendering"/> 驱动（跟随显示器刷新率），
/// 虫群层隐藏时降频到 <see cref="LifecycleEngine.IdleTickHz"/>。
/// </summary>
public partial class App : System.Windows.Application, IAppHost
{
    /// <summary>Windows 控制台代码页 UTF-8 —— 中文报告被重定向时才好读。</summary>
    private const uint CP_UTF8 = 65001;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint codePage);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SimulationPacer _pacer = new(LifecycleEngine.TickHz);
    private double _lastTickAt;
    private double _nextTrayUpdate;
    private double _nextViewRefresh;
    private bool _shuttingDown;

    private PersistenceService _persistence = null!;
    private TrayService _tray = null!;
    private NotificationService _notifications = null!;
    private SoundService _sound = null!;
    private AppState _state = new();

    private OverlayWindow? _overlay;
    private ControlPanelWindow? _panel;
    private TaskListWindow? _taskList;
    private HoverBubbleWindow? _hoverBubble;

    private readonly List<Action> _viewRefreshHandlers = new();
    private readonly DispatcherTimer _idleTimer;

    /// <summary>每日任务：最近一次检查的日期键 + 下次允许检查的时刻（最多每 60 秒一次）。</summary>
    private string _lastDailyKey = string.Empty;
    private double _nextDailyCheck;

    /// <summary>每周任务：最近一次检查的 ISO 周键 + 下次允许检查的时刻（最多每 60 秒一次）。</summary>
    private string _lastWeeklyKey = string.Empty;
    private double _nextWeeklyCheck;

    private const double DailyCheckIntervalSeconds = 60.0;
    private const double WeeklyCheckIntervalSeconds = 60.0;

    /// <summary>最近一帧的任务快照，本帧内共用，避免重复克隆。</summary>
    private IReadOnlyList<TaskItem> _frameSnapshot = Array.Empty<TaskItem>();

    public App()
    {
        // WinExe 没有真正的控制台流，重定向到文件时默认编码不是 UTF-8，
        // 中文报告会被写成 GBK 字节、再被按 UTF-8 读成乱码。
        // 这里显式换成带 BOM 的 UTF-8 写入器 —— 纯 BCL，无需 NuGet，失败也无所谓。
        try
        {
            SetConsoleOutputCP(CP_UTF8);
            SetConsoleCP(CP_UTF8);
        }
        catch
        {
            // 无控制台：忽略
        }

        try
        {
            var utf8 = new System.IO.StreamWriter(Console.OpenStandardOutput(),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = true,
            };
            Console.SetOut(utf8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MaggotTask] 切换 stdout 编码失败: {ex.Message}");
        }

        _idleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / LifecycleEngine.IdleTickHz),
        };
        // 悬浮层关闭时降频到 IDLE_TICK_HZ：时钟源换成 10Hz 的 DispatcherTimer，
        // 引擎仍按 TICK_HZ 的固定步长推进（一次补多步），所以腐烂速度不随帧率变化。
        _idleTimer.Tick += (_, _) => GameTick();
    }

    // ------------------------------------------------------------------
    // IAppHost
    // ------------------------------------------------------------------

    public LifecycleEngine Engine { get; private set; } = null!;

    public MaggotConfig Config => _state.Config;

    public SoundService Sound => _sound;

    public bool ReduceMotion { get; private set; }

    public bool SwarmVisible => _overlay is { IsVisible: true };

    public IReadOnlyList<TaskItem> TaskSnapshot() => Engine.Snapshot();

    public int TaskCount => Engine.Count;

    public int FlyCount => Engine.FlyCount;

    public (int Egg, int Larva, int Pupa, int Fly, int Dead) StageCounts() => Engine.StageCounts();

    public double AverageRot => Engine.AverageRot;

    public long SquashedCount => Engine.SquashedCount;

    public string RemainingText(float rot) => Engine.RemainingText(rot);

    /// <summary>今天（设备本地日期）的日期键 —— 规格 §12.2 明确要求用本地日期，不能用 UTC。</summary>
    public string TodayKey => DailyKeys.Today();

    // ------------------------------------------------------------------
    // 每日任务（规格 §12）
    // ------------------------------------------------------------------

    public IReadOnlyList<DailyTemplate> DailyTemplates() => Engine.DailyTemplates();

    public DailyTemplate? AddDailyTemplate(string title, out int spawnedToday)
    {
        spawnedToday = 0;
        long now = LifecycleEngine.NowMs();
        int before = Engine.Count;
        DailyTemplate? t = Engine.AddDailyTemplate(title, string.Empty, now);
        if (t is null) return null;

        spawnedToday = Engine.Count - before;   // 规格 §12.4：新增后必须立刻有今天的实例
        HandleSpawnEvents();
        MarkDirty();
        RefreshViews();
        return t;
    }

    public void SetDailyTemplateEnabled(string templateId, bool enabled)
    {
        if (!Engine.SetDailyTemplateEnabled(templateId, enabled)) return;
        // 重新启用后应立刻补上今天的实例（规格 §12.4：停用只是不再生成新的）。
        if (enabled)
        {
            Engine.EnsureDailyTasks(DailyKeys.Today(), LifecycleEngine.NowMs());
            HandleSpawnEvents();
        }
        MarkDirty();
        RefreshViews();
    }

    public void RemoveDailyTemplate(string templateId)
    {
        // 实例**不删** —— 它们退化成普通任务继续烂（规格 §12.4）。
        if (!Engine.RemoveDailyTemplate(templateId)) return;
        MarkDirty();
        RefreshViews();
    }

    public string DailyInstanceStatus(DailyTemplate template) =>
        LifecycleEngine.DailyInstanceStatusText(
            Engine.FindDailyInstance(template.Id, DailyKeys.Today()));

    // ------------------------------------------------------------------
    // 每周任务（规格 §15）
    // ------------------------------------------------------------------

    /// <summary>本周（设备本地日期）的 ISO 周键 —— 规格 §15.3 明确要求本地日期 + 周基准年。</summary>
    public string ThisWeekKey => WeeklyKeys.Today();

    public IReadOnlyList<WeeklyTemplate> WeeklyTemplates() => Engine.WeeklyTemplates();

    public WeeklyTemplate? AddWeeklyTemplate(string title, int weekday, out int spawnedThisWeek)
    {
        spawnedThisWeek = 0;
        long now = LifecycleEngine.NowMs();
        int before = Engine.Count;
        WeeklyTemplate? t = Engine.AddWeeklyTemplate(title, weekday, string.Empty, now);
        if (t is null) return null;

        // 规格 §15.6-1：今天已经到达该星期，新增后必须**立刻**看得见这只虫；
        // §15.6-2：还没到那天则一个都不生成。
        spawnedThisWeek = Engine.Count - before;
        HandleSpawnEvents();
        MarkDirty();
        RefreshViews();
        return t;
    }

    public void SetWeeklyTemplateEnabled(string templateId, bool enabled)
    {
        if (!Engine.SetWeeklyTemplateEnabled(templateId, enabled)) return;
        // 重新启用后应立刻补上本周的实例（§15.5 → §12.4；迟到也补，不跳过这一周）。
        if (enabled)
        {
            Engine.EnsureWeeklyTasks(LifecycleEngine.NowMs());
            HandleSpawnEvents();
        }
        MarkDirty();
        RefreshViews();
    }

    public void RemoveWeeklyTemplate(string templateId)
    {
        // 实例**不删** —— 它们退化成普通任务继续烂（规格 §15.5 → §12.4）。
        if (!Engine.RemoveWeeklyTemplate(templateId)) return;
        MarkDirty();
        RefreshViews();
    }

    public string WeeklyInstanceStatus(WeeklyTemplate template) =>
        LifecycleEngine.WeeklyInstanceStatusText(
            Engine.FindWeeklyInstance(template.Id, WeeklyKeys.Today()));

    /// <summary>消费模板生成事件（发通知 + 播放孵化音）。每日 / 每周走同一条路径。</summary>
    private void HandleSpawnEvents()
    {
        List<EngineEvent> events = Engine.DrainEvents();
        if (events.Count == 0) return;

        List<EngineEvent> spawns = events.FindAll(
            e => e.Type is EngineEventType.DailySpawned or EngineEventType.WeeklySpawned);
        if (spawns.Count == 0) return;

        foreach (EngineEvent ev in spawns) _sound.PlayHatch();
        _notifications.Handle(spawns);
    }

    /// <summary>
    /// 规格 §12.2：每次 tick（或至少每 60 秒）调用一次。
    /// 这里在渲染循环里做**极廉价**的判断 —— 缓存上次的日期键并比对，
    /// 日期没变且没到 60 秒，直接返回，一次字符串比较而已。
    /// </summary>
    private void EnsureDailyTasksTicked(double now, bool force)
    {
        if (!force)
        {
            if (now < _nextDailyCheck) return;
            _nextDailyCheck = now + DailyCheckIntervalSeconds;
        }

        string todayKey = DailyKeys.Today();
        if (!force && string.Equals(todayKey, _lastDailyKey, StringComparison.Ordinal)) return;

        int created = Engine.EnsureDailyTasks(todayKey, LifecycleEngine.NowMs());
        _lastDailyKey = todayKey;
        if (created > 0)
        {
            HandleSpawnEvents();
            MarkDirty();
            RefreshViews();
        }
    }

    /// <summary>
    /// 规格 §15.4：每次 tick（或至少每 60 秒）调用一次。
    ///
    /// 与每日任务同一套做法（逐行同构）：先做一次极廉价的节流判断 ——
    /// 60 秒内直接返回，一次比较开销都没有；到了节流点再算本周周键，
    /// 与缓存比对：**周键变了就必须真的检查一次**（跨周时不会被「键没变」短路掉），
    /// 没变则廉价返回。所以 EnsureWeeklyTasks 至多每 60 秒被调用一次。
    /// </summary>
    private void EnsureWeeklyTasksTicked(double now, bool force)
    {
        if (!force)
        {
            if (now < _nextWeeklyCheck) return;
            _nextWeeklyCheck = now + WeeklyCheckIntervalSeconds;
        }

        // 周键与星期几都取自**本地**时间戳（规格 §15.3：绝不用 UTC）。
        long nowMs = LifecycleEngine.NowMs();
        string weekKey = WeeklyKeys.FromUnixMs(nowMs);
        if (!force && string.Equals(weekKey, _lastWeeklyKey, StringComparison.Ordinal)) return;

        int created = Engine.EnsureWeeklyTasks(
            weekKey, WeeklyKeys.IsoWeekdayFromUnixMs(nowMs), nowMs);
        _lastWeeklyKey = weekKey;
        if (created > 0)
        {
            HandleSpawnEvents();
            MarkDirty();
            RefreshViews();
        }
    }

    public void MarkDirty()
    {
        if (_shuttingDown) return;
        _state.LastSeenAt = LifecycleEngine.NowMs();
        _persistence.MarkDirty(_state);
    }

    public IDisposable SubscribeViewRefresh(Action onRefresh)
    {
        _viewRefreshHandlers.Add(onRefresh);
        return new Unsubscriber(() => _viewRefreshHandlers.Remove(onRefresh));
    }

    /// <summary>立刻刷新所有已打开界面的绑定（诊断用）。</summary>
    public void SyncViews() => RefreshViews();

    /// <summary>托盘「显示/隐藏控制面板」菜单项当前文案（诊断用）。</summary>
    public string TrayPanelMenuText => _tray.PanelMenuText;

    private sealed class Unsubscriber : IDisposable
    {
        private readonly Action _dispose;
        public Unsubscriber(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }

    // ------------------------------------------------------------------
    // 启动
    // ------------------------------------------------------------------

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 无界面自测：跑完判定标准直接退出，不开窗口、不建托盘图标。
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            int code = SelfTest.RunAll();
            Environment.Exit(code);
            return;
        }


        bool smoke = e.Args.Any(a => a.Equals("--smoke", StringComparison.OrdinalIgnoreCase));
        bool diagUi = e.Args.Any(a => a.Equals("--diag-ui", StringComparison.OrdinalIgnoreCase));
        bool diagHover = e.Args.Any(a => a.Equals("--diag-hover", StringComparison.OrdinalIgnoreCase));
        bool diagSound = e.Args.Any(a => a.Equals("--diag-sound", StringComparison.OrdinalIgnoreCase));
        bool diagWeekly = e.Args.Any(a => a.Equals("--diag-weekly", StringComparison.OrdinalIgnoreCase));
        bool diagFps = e.Args.Any(a => a.Equals("--diag-fps", StringComparison.OrdinalIgnoreCase));
        bool seedDemo = e.Args.Any(a => a.Equals("--seed-demo", StringComparison.OrdinalIgnoreCase));
        string? stageArg = e.Args.FirstOrDefault(a => a.StartsWith("--stage=", StringComparison.OrdinalIgnoreCase));

        // 阴影模式必须在 Bootstrap 之前定下来 —— 覆盖层在构造时就据此决定挂不挂 Effect。
        // --shadow=blur 用来切回旧的整层全屏模糊，做 A/B 对照。
        string? shadowArg = e.Args.FirstOrDefault(a => a.StartsWith("--shadow=", StringComparison.OrdinalIgnoreCase));
        if (shadowArg is not null)
        {
            string mode = shadowArg["--shadow=".Length..].Trim().ToLowerInvariant();
            CreaturePainter.Shadow = mode switch
            {
                "blur" or "blurlayer" or "legacy" => CreaturePainter.ShadowMode.BlurLayer,
                "none" or "off" => CreaturePainter.ShadowMode.None,
                "solid" => CreaturePainter.ShadowMode.Solid,
                _ => CreaturePainter.ShadowMode.SoftGradient,
            };
        }

        try
        {
            Bootstrap(smoke);
            if (seedDemo || diagHover) SeedDemoTasks();
            if (diagSound) { RunSoundDiagnostics(); ExitApp(); return; }
            if (diagFps)
            {
                // --diag-fps [生物数]  默认 120 只，覆盖卵/蛆/蛹/苍蝇四个绘制分支。
                int count = 120;
                int i = Array.FindIndex(e.Args, a => a.Equals("--diag-fps", StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out int n) && n > 0)
                {
                    count = n;
                }
                SeedSyntheticTasks(count, stageArg?["--stage=".Length..].Trim().ToLowerInvariant());
                StartFpsDiagnostics();
                return;
            }
            if (diagHover)
            {
                // 标签页/合成器都还没跑起来时，先把钩子挂上，再等几帧让布局稳定。
                Dispatcher.BeginInvoke(new Action(RunHoverDiagnostics), DispatcherPriority.ApplicationIdle);
                return;
            }
            if (diagWeekly)
            {
                // 需要真实的可视树（每周任务区 + 逾期标记），所以等布局跑完再诊断。
                Dispatcher.BeginInvoke(new Action(RunWeeklyDiagnostics), DispatcherPriority.ApplicationIdle);
                return;
            }
            if (diagUi) StartUiDiagnostics();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"启动失败：\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                "蛆虫任务",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void Bootstrap(bool smoke)
    {
        // 1) 持久化 + 状态
        _persistence = new PersistenceService(TimeSpan.FromSeconds(5));
        _state = _persistence.Load();
        _state.Config.Normalize();

        // 2) 引擎
        Engine = new LifecycleEngine(_state.Config);
        Engine.ReplaceAll(_state.Tasks, _state.SquashedCount);
        Engine.ReplaceDailyTemplates(_state.DailyTemplates);
        Engine.ReplaceWeeklyTemplates(_state.WeeklyTemplates);

        // 3) 减少动效：系统设置优先，配置可覆盖（规格未定义此项，属判断）
        ReduceMotion = _state.Config.ReduceMotionOverride ?? !SystemParameters.ClientAreaAnimation;

        // 4) 服务
        _sound = new SoundService { Enabled = _state.Config.SoundEnabled };
        _tray = new TrayService();
        _notifications = new NotificationService(_tray) { Enabled = _state.Config.NotifyOnFly };
        WireTray();

        // 5) 启动补算离线腐烂；补算期间产生的阶段事件静默丢弃，只走上方的汇总通知
        double offline = Engine.ApplyOfflineDecay(_state.LastSeenAt, LifecycleEngine.NowMs());
        Engine.DrainEvents();

        List<TaskItem> alreadyFlies = Engine.TasksAlreadyFlies();
        _state.SquashedCount = Engine.SquashedCount;
        _notifications.SummarizeStartup(alreadyFlies, offline);

        // 6) 窗口
        _overlay = new OverlayWindow { ReduceMotion = ReduceMotion };
        _overlay.CreatureSquashed += OnCreatureSquashed;
        _overlay.Show();
        _overlay.ApplyClickThrough(_state.Config.ClickThrough);

        _panel = new ControlPanelWindow(this, _state.PanelLeft, _state.PanelTop);
        _panel.PositionChanged += (l, t) =>
        {
            _state.PanelLeft = l;
            _state.PanelTop = t;
            MarkDirty();
        };
        _panel.Show();
        _panel.ViewModel.SetStatusLine(StatusLine(offline));
        _tray.SetPanelVisible(true);

        // 6c) 悬停信息气泡（规格 §14）。它是独立小窗，样式在 SourceInitialized 里落位。
        _hoverBubble = new HoverBubbleWindow(
            CollectHoverTargets,
            CurrentCursorLocal,
            () => _clock.Elapsed.TotalSeconds);
        _hoverBubble.Show();
        _hoverBubble.Hide();

        // 先同步一次集合与界面（不推进引擎，节拍器从零开始计时）。
        SyncOnly();

        // 6b) 每日任务：启动时先强制对一次「今天」，用户一打开就能看见今天那只虫。
        EnsureDailyTasksTicked(0.0, force: true);
        _nextDailyCheck = _clock.Elapsed.TotalSeconds + DailyCheckIntervalSeconds;

        // 6b-2) 每周任务（规格 §15.4）：同样先强制对一次「本周」。
        EnsureWeeklyTasksTicked(0.0, force: true);
        _nextWeeklyCheck = _clock.Elapsed.TotalSeconds + WeeklyCheckIntervalSeconds;

        // 7) 主循环：可见时由覆盖层的 CompositionTarget.Rendering 驱动，
        //    隐藏时降频到 IDLE_TICK_HZ。
        _lastTickAt = _clock.Elapsed.TotalSeconds;
        _overlay.Frame += OnOverlayFrame;
        _idleTimer.Start();

        SaveNow();

        if (smoke)
        {
            DispatcherTimer killer = new(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromSeconds(12),
            };
            killer.Tick += (_, _) =>
            {
                killer.Stop();
                SafeWriteLine("[smoke] 12 秒运行正常，退出。");
                ExitApp();
            };
            killer.Start();
        }

        SafeWriteLine($"[MaggotTask] 启动完成：任务 {Engine.Count}，苍蝇 {Engine.FlyCount}，" +
                      $"每日模板 {Engine.DailyTemplates().Count}，每周模板 {Engine.WeeklyTemplates().Count}，" +
                      $"本周 {WeeklyKeys.Today()}，" +
                      $"离线补算 {offline:0} 秒，减少动效 {ReduceMotion}，穿透 {_state.Config.ClickThrough}");
    }

    private string StatusLine(double offlineSeconds)
    {
        if (offlineSeconds >= 60) return $"离线补算 {offlineSeconds / 60.0:0.#} 分钟 · 刚同步";
        if (offlineSeconds > 1) return $"离线补算 {offlineSeconds:0} 秒 · 刚同步";
        return "刚同步 · 状态已落盘";
    }

    private void WireTray()
    {
        _tray.TogglePanelRequested += TogglePanel;
        _tray.ToggleSwarmRequested += ToggleSwarm;
        _tray.ToggleRunningRequested += ToggleRunning;
        _tray.OpenTaskListRequested += OpenTaskList;
        _tray.ExitRequested += ExitApp;
        _tray.DoubleClickRequested += ShowPanel;
        _tray.BalloonClicked += ShowPanel;
        _tray.UpdateStatus(_state.Config.Running, Engine.Count, Engine.FlyCount);
        _tray.SetPanelVisible(_panel?.IsVisible ?? false);
    }

    // ------------------------------------------------------------------
    // 主循环
    // ------------------------------------------------------------------

    private void OnOverlayFrame(double now, double dt) => GameTick(dt, now);

    /// <summary>只同步虫群集合与界面，不推进引擎（启动时用）。</summary>
    private void SyncOnly()
    {
        _frameSnapshot = Engine.Snapshot();
        _overlay?.Model.Sync(_frameSnapshot, _clock.Elapsed.TotalSeconds);
        _tray.UpdateStatus(_state.Config.Running, Engine.Count, Engine.FlyCount);
        RefreshViews();
    }

    // ------------------------------------------------------------------
    // 悬停查看任务（规格 §14）
    //
    // 虫群层是 WS_EX_TRANSPARENT，收不到鼠标消息 —— 所以这里**不装全局钩子**，
    // 也不订阅鼠标事件，只在主循环里按 ~10Hz 轮询 GetCursorPos（Cursor.Position）。
    // ------------------------------------------------------------------

    /// <summary>全局光标位置换算到覆盖层本地坐标（覆盖层左上角 = 虚拟桌面左上角）。</summary>
    private Point CurrentCursorLocal()
    {
        try
        {
            System.Drawing.Point p = System.Windows.Forms.Cursor.Position;
            double left = _overlay?.Left ?? 0.0;
            double top = _overlay?.Top ?? 0.0;
            return new Point(p.X - left, p.Y - top);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MaggotTask] 读取光标位置失败: {ex.Message}");
            return new Point(double.NegativeInfinity, double.NegativeInfinity);
        }
    }

    private readonly List<HoverTarget> _hoverTargets = new();

    /// <summary>当帧全部生物的位置快照（命中半径按 §14.1 取 size × 0.7）。</summary>
    private IReadOnlyList<HoverTarget> CollectHoverTargets()
    {
        _hoverTargets.Clear();
        if (_overlay is null) return _hoverTargets;

        foreach (CreatureVisual c in _overlay.Model.Creatures)
        {
            _hoverTargets.Add(new HoverTarget(c.TaskId, c.Position, c.Size));
        }
        return _hoverTargets;
    }

    /// <summary>每帧调用；气泡内部自己限流到约 10Hz（规格 §14.1）。</summary>
    private void HoverTick()
    {
        if (_hoverBubble is null) return;

        // 虫群层不可见 / 没有生物 → 直接收起，避免残留气泡（验收标准 4）。
        if (_overlay is not { IsVisible: true } || _overlay.Model.Count == 0)
        {
            _hoverBubble.Tracker.Reset();
            if (_hoverBubble.IsVisible) _hoverBubble.Hide();
            return;
        }

        _hoverBubble.Tick();

        if (!_hoverBubble.Tracker.IsShown)
        {
            if (_hoverBubble.IsVisible) _hoverBubble.Hide();
            return;
        }

        string? id = _hoverBubble.Tracker.HoveredId;
        if (id is null) return;

        TaskItem? task = Engine.FindById(id);
        if (task is null)
        {
            // 任务被拍死/删除 → 立刻收起
            _hoverBubble.Tracker.Reset();
            _hoverBubble.Hide();
            return;
        }

        _hoverBubble.UpdateContent(task, DailyKeys.Today(), WeeklyKeys.Today());
    }

    /// <summary>
    /// 单次主循环。引擎按 <see cref="LifecycleEngine.TickHz"/> 固定步长推进，
    /// 渲染频率（显示器刷新率）与模拟频率解耦。
    /// </summary>
    /// <param name="dtOverride">
    /// 由渲染帧传入的真实帧间隔；为 null 时（悬浮层隐藏，走 10Hz 兜底定时器）
    /// 由 <see cref="SimulationPacer"/> 自行切成固定步长。
    /// </param>
    private void GameTick(double? dtOverride = null, double? nowOverride = null)
    {
        if (_shuttingDown) return;

        double now = nowOverride ?? _clock.Elapsed.TotalSeconds;
        _lastTickAt = now;

        int steps;
        double simDt;

        if (dtOverride is > 0)
        {
            // 有渲染帧：按真实帧间隔，但仍以固定步长整份推进。
            double dt = Math.Min(dtOverride.Value, LifecycleEngine.MaxDt);
            steps = Math.Max(1, (int)Math.Round(dt / _pacer.StepSeconds));
            simDt = dt / steps;
        }
        else
        {
            // 隐藏模式：由节拍器按真实经过时间算出该补几步。
            //
            // ⚠️ 时钟唯一归属（规格「主循环」节）：悬浮层可见时，引擎推进**只**归
            // 渲染帧回调（OnOverlayFrame → dtOverride 路径）。这条 10Hz 计时器路径
            // 若照常 Advance，就是第二条时钟源 —— 腐烂速度会变成规格的约 2 倍
            // （可见期每秒被推进 2 秒）。所以可见时这里只重置节拍器
            // （丢弃累计时间，避免隐藏后的第一次 Advance 把可见期整段补算），
            // 引擎/同步/悬停/托盘一律由 60fps 的帧路径负责。
            if (_overlay is { IsVisible: true })
            {
                _pacer.Reset();
                return;
            }

            steps = _pacer.Advance();
            simDt = _pacer.StepSeconds;
        }

        for (int i = 0; i < steps; i++)
        {
            Engine.Tick(simDt, LifecycleEngine.NowMs());
        }

        _frameSnapshot = Engine.Snapshot();

        if (_overlay is not null)
        {
            // 可见时位置推进由覆盖层的渲染回调负责；这里只做集合同步。
            // 隐藏时兜底推进，保证虫子不会「停摆」。
            if (_overlay.IsVisible)
            {
                _overlay.Model.Sync(_frameSnapshot, now);
            }
            else
            {
                _overlay.Model.Sync(_frameSnapshot, now);
                _overlay.Model.Update(now, _pacer.StepSeconds, ReduceMotion);
            }
        }

        List<EngineEvent> events = Engine.DrainEvents();
        if (events.Count > 0)
        {
            _notifications.Handle(events);
            foreach (EngineEvent ev in events)
            {
                switch (ev.Type)
                {
                    case EngineEventType.Hatched:
                        _sound.PlayHatch();
                        break;
                    case EngineEventType.BecameFly:
                        _sound.PlayAlert();
                        break;
                    case EngineEventType.Bred:
                        _sound.PlayBred();
                        break;
                }
            }
            MarkDirty();
        }

        // 规格 §12.2：每次 tick（至少每 60 秒）检查一次每日实例。
        EnsureDailyTasksTicked(now, force: false);

        // 规格 §15.4：每次 tick（至少每 60 秒）检查一次每周实例。
        EnsureWeeklyTasksTicked(now, force: false);

        // 规格 §14：轮询光标（内部限流到约 10Hz），驱动悬停气泡。
        HoverTick();

        // 规格 §14.3：暂停时生物不动，悬停依然有效 —— 所以这里**不**判断 Running。

        if (now >= _nextTrayUpdate)
        {
            _nextTrayUpdate = now + 1.0;
            _tray.UpdateStatus(_state.Config.Running, Engine.Count, Engine.FlyCount);
        }

        if (now >= _nextViewRefresh)
        {
            _nextViewRefresh = now + 0.20;
            RefreshViews();
        }
    }

    private void RefreshViews()
    {
        Action[] handlers = _viewRefreshHandlers.ToArray();
        foreach (Action a in handlers)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MaggotTask] 刷新界面回调异常: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------
    // IAppHost 动作
    // ------------------------------------------------------------------

    public void ToggleRunning()
    {
        _state.Config.Running = !_state.Config.Running;
        MarkDirty();
        _tray.UpdateStatus(_state.Config.Running, Engine.Count, Engine.FlyCount);
        RefreshViews();
    }

    public void TogglePanel()
    {
        if (_panel is null) return;
        if (_panel.IsVisible) _panel.Hide();
        else ShowPanel();
        _tray.SetPanelVisible(_panel.IsVisible);
    }

    /// <summary>
    /// 面板显隐变化时由 <see cref="ControlPanelWindow"/> 调用。
    /// 面板上的「收起」按钮和托盘菜单是同一条路径，所以两边文案永远同步。
    /// </summary>
    public void NotifyPanelVisibilityChanged()
    {
        _tray.SetPanelVisible(_panel?.IsVisible ?? false);
    }

    private void ShowPanel()
    {
        if (_panel is null) return;
        if (!_panel.IsVisible) _panel.Show();
        _panel.Activate();
        _panel.Topmost = true;
        _tray.SetPanelVisible(true);
    }

    public void ToggleSwarm()
    {
        if (_overlay is null) return;
        if (_overlay.IsVisible)
        {
            _overlay.Hide();
            // 从"帧驱动"切回"节拍器驱动"：丢弃可见期累积的时间基准，
            // 防止隐藏后的第一次 Advance 把整段可见期一次性补算。
            _pacer.Reset();
        }
        else
        {
            _overlay.UpdateBounds();
            _overlay.Show();
            _overlay.ApplyClickThrough(_state.Config.ClickThrough);
        }
        RefreshViews();
    }

    public void ApplyClickThrough() => _overlay?.ApplyClickThrough(_state.Config.ClickThrough);

    public void OpenTaskList()
    {
        if (_taskList is null)
        {
            _taskList = new TaskListWindow(this);
            _taskList.Closed += (_, _) => _taskList = null;
            SubscribeViewRefresh(() => _taskList?.ViewModel.Refresh());
        }

        if (!_taskList.IsVisible) _taskList.Show();
        _taskList.Activate();
        _taskList.Topmost = true;
        _taskList.ViewModel.Refresh();
    }

    public bool NewTask(string title)
    {
        TaskItem? t = Engine.AddTask(title);
        if (t is null) return false;
        MarkDirty();
        RefreshViews();
        return true;
    }

    public void Care(string taskId)
    {
        // 规格 §13 只定义了四个音效（拍死 / 苍蝇嗡鸣 / 孵化 / 繁殖），
        // 没有为「我去做了」定义音效 —— 所以这里**故意不播放任何声音**，
        // 而不是自己发明一个规格外的音效。
        Engine.Care(taskId, LifecycleEngine.NowMs());
        Engine.DrainEvents();
        MarkDirty();
        RefreshViews();
    }

    public void Complete(string taskId)
    {
        if (!Engine.Complete(taskId, LifecycleEngine.NowMs(), out _)) return;

        _state.SquashedCount = Engine.SquashedCount;
        _sound.PlaySquash();
        Engine.DrainEvents(); // Squashed 由界面直接反馈，不需要通知
        MarkDirty();
        RefreshViews();
    }

    public void Delete(string taskId)
    {
        if (!Engine.RemoveTask(taskId)) return;
        MarkDirty();
        RefreshViews();
    }

    public void ResetAllTasks()
    {
        Engine.ResetAll(LifecycleEngine.NowMs());
        Engine.DrainEvents();
        MarkDirty();
        RefreshViews();
    }

    public void ClearAllTasks()
    {
        Engine.ClearAll();
        Engine.DrainEvents();
        _state.SquashedCount = 0;
        _overlay?.Model.Clear();
        MarkDirty();
        RefreshViews();
    }

    /// <summary>覆盖层里被「拍」到的虫子。</summary>
    private void OnCreatureSquashed(string taskId, Stage stage)
    {
        _overlay?.Model.RemoveAt(taskId, stage, _clock.Elapsed.TotalSeconds);
        Complete(taskId);
    }

    // ------------------------------------------------------------------
    // 退出
    // ------------------------------------------------------------------

    public void ExitApp() => Shutdown();

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        if (_overlay is not null) _overlay.Frame -= OnOverlayFrame;
        _idleTimer.Stop();

        try
        {
            _state.Tasks = Engine.Snapshot().ToList();
            _state.DailyTemplates = Engine.DailyTemplates().ToList();
            _state.WeeklyTemplates = Engine.WeeklyTemplates().ToList();
            _state.SquashedCount = Engine.SquashedCount;
            _persistence.Flush(_state);
            SafeWriteLine($"[MaggotTask] 退出：已写入 {AppPaths.StateFile}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MaggotTask] 退出落盘失败: {ex.Message}");
        }

        _persistence.Dispose();
        _tray.Dispose();
        _hoverBubble?.Close();
        _overlay?.Close();

        base.OnExit(e);
    }

    private void SaveNow()
    {
        _state.Tasks = Engine.Snapshot().ToList();
        _state.DailyTemplates = Engine.DailyTemplates().ToList();
        _state.WeeklyTemplates = Engine.WeeklyTemplates().ToList();
        _state.SquashedCount = Engine.SquashedCount;
        _persistence.Flush(_state);
    }

    /// <summary>WinExe 下 stdout 不总是存在，写不进去就当无事发生。</summary>
    internal static void SafeWriteLine(string text)
    {
        Debug.WriteLine(text);
        try
        {
            Console.WriteLine(text);
            Console.Out.Flush();
        }
        catch
        {
            // 无控制台：忽略
        }
    }

    /// <summary>
    /// 交付验证用：往引擎里塞一批覆盖全部阶段的虫子（含繁殖出的后代），
    /// 便于人工/脚本检查渲染与列表。仅在显式传入 --seed-demo 时执行。
    /// </summary>
    private void SeedDemoTasks()
    {
        long now = LifecycleEngine.NowMs();
        (string Title, float Rot, int Gen)[] seeds =
        {
            ("写周报", 0.02f, 0),
            ("回邮件", 0.12f, 0),
            ("改 bug #1421", 0.26f, 0),
            ("看那篇论文", 0.44f, 0),
            ("预约体检", 0.58f, 0),
            ("整理桌面文件", 0.83f, 0),
            ("跟房东确认水电", 0.95f, 0),
            ("蝇卵 #1 · 写周报", 0.20f, 1),
            ("蝇卵 #2 · 改 bug #14", 0.70f, 2),
        };

        foreach ((string title, float rot, int gen) in seeds)
        {
            TaskItem? t = Engine.AddTask(title, string.Empty, now);
            if (t is null) continue;
            t.Rot = rot;
            t.Generation = gen;
            t.Gene = LifecycleEngine.GeneFor(gen);
            if (gen > 0) t.ParentId = "demo-parent";
            if (rot >= LifecycleEngine.HatchT) t.Status = TaskStatus.Doing;
            t.Progress = (int)(rot * 100);
        }

        // 一条已拍死的任务，验证「战果」分组
        TaskItem? done = Engine.AddTask("倒垃圾", string.Empty, now);
        if (done is not null)
        {
            done.Rot = 0.30f;
            Engine.Complete(done.Id, now, out _);
        }

        Engine.DrainEvents();
        SyncOnly();
        MarkDirty();
        SafeWriteLine($"[seed-demo] 已注入 {Engine.Count} 个任务（苍蝇 {Engine.FlyCount}）");
    }

    // ------------------------------------------------------------------
    // --diag-fps：注入 N 只生物，测「实际帧间隔」。
    //
    // 为什么需要它：旧报告里的「中位绘制耗时 2ms/帧」只量了绘制指令的**构建**，
    // 量不到 WPF 渲染线程上的光栅化与 Effect 求值 —— 而卡顿恰恰出在那里。
    // 这个诊断量的是相邻帧的真实间隔，也就是用户真正感知到的流畅度。
    // ------------------------------------------------------------------

    private void SeedSyntheticTasks(int count, string? forceStage = null)
    {
        long now = LifecycleEngine.NowMs();
        for (int i = 0; i < count; i++)
        {
            TaskItem? t = Engine.AddTask($"压测任务 {i + 1}", string.Empty, now);
            if (t is null) break;

            // --stage=<egg|larva|pupa|fly> 把所有生物钉在同一个阶段，
            // 用来定位到底哪种生物的绘制最贵。默认则四阶段混合。
            t.Rot = forceStage switch
            {
                "egg" => 0.02f,
                "larva" => 0.30f,
                "pupa" => 0.62f,
                "fly" => 0.92f,
                _ => (i % 8) switch
                {
                    0 => 0.02f,
                    1 or 2 or 3 => 0.30f,
                    4 or 5 => 0.62f,
                    _ => 0.92f,
                },
            };
            if (t.Rot >= LifecycleEngine.HatchT) t.Status = TaskStatus.Doing;
            t.Progress = (int)(t.Rot * 100);
        }

        Engine.DrainEvents();
        SyncOnly();
        SafeWriteLine($"[diag-fps] 已注入 {Engine.Count} 只生物（stage={forceStage ?? "混合"}）");
    }

    private void StartFpsDiagnostics()
    {
        // 先等 2 秒：生物要从屏幕外飞入，入场淡入 0.45 秒，等稳定了再开始测。
        DispatcherTimer warm = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(2) };
        warm.Tick += (_, _) =>
        {
            warm.Stop();
            _overlay?.ResetFrameStats();

            DispatcherTimer probe = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(8) };
            probe.Tick += (_, _) =>
            {
                probe.Stop();

                double fps = _overlay?.AchievedFps ?? 0;
                System.Text.StringBuilder r = new();
                r.AppendLine("══════ 帧率诊断（真实帧间隔）══════");
                r.AppendLine($"生物数              : {_overlay?.Model.Count ?? 0}");
                r.AppendLine($"阴影模式            : {CreaturePainter.ShadowModeDescription}");
                r.AppendLine($"实际帧率(由中位间隔): {fps:0.0} fps");
                r.AppendLine($"帧间隔 中位         : {_overlay?.MedianFrameIntervalMs:0.00} ms");
                r.AppendLine($"帧间隔 P95          : {_overlay?.P95FrameIntervalMs:0.00} ms");
                r.AppendLine($"帧间隔 最差         : {_overlay?.WorstIntervalMs:0.00} ms");
                r.AppendLine($"总帧数              : {_overlay?.FrameCount ?? 0}");
                r.AppendLine($"绘制指令耗时(中位)  : {_overlay?.MedianFrameMs:0.00} ms  ← 只含指令构建，不含光栅化");
                SafeWriteLine(r.ToString());

                ExitApp();
            };
            probe.Start();
        };
        warm.Start();
    }

    // ------------------------------------------------------------------
    // --diag-sound：把程序化音效的缓冲区事实（时长 / 峰值 / RMS / WAV 头）打出来。
    // 人耳听不到的部分，用可核对的数字替代。跑完立刻退出。
    // ------------------------------------------------------------------

    private void RunSoundDiagnostics()
    {
        StringBuilder sb = new();
        sb.AppendLine("══════ 音效诊断（规格 §13）══════");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"采样率: {SoundService.SampleRate} Hz · 16-bit 单声道 PCM · 44 字节 WAV 头");
        sb.AppendLine($"连点保护: 同一音效 {SoundService.RetriggerGuardMs}ms 内不重复触发");
        sb.AppendLine($"音效开关默认值: {new MaggotConfig().SoundEnabled}（规格要求 true）");
        sb.AppendLine();

        foreach (SoundService.SoundStats s in _sound.Stats())
        {
            sb.AppendLine($"[{s.Name}]");
            sb.AppendLine($"  时长        : {s.DurationMs:0} ms（规格要求 {ExpectedDuration(s.Name)} ms）");
            sb.AppendLine($"  采样数      : {s.SampleCount}");
            sb.AppendLine($"  字节数      : {s.ByteLength}（44 头 + {s.ByteLength - 44} 数据）");
            sb.AppendLine($"  峰值振幅    : {s.Peak:0.0000}");
            sb.AppendLine($"  RMS 振幅    : {s.Rms:0.0000}");
            sb.AppendLine($"  是否静音    : {(s.Peak < 1e-6 ? "是（不合格）" : "否")}");
            sb.AppendLine($"  WAV 头有效  : {s.ValidWavHeader}");
        }

        sb.AppendLine();
        sb.AppendLine("── WAV 头逐字节校验（每个音效独立校验）──");
        IReadOnlyList<SoundService.SoundStats> stats = _sound.Stats();
        for (int i = 0; i < stats.Count; i++)
        {
            byte[] wav = _sound.Bytes(i);
            bool ok = SoundService.IsValidWav(wav, out string reason);
            string riff = System.Text.Encoding.ASCII.GetString(wav, 0, 4);
            string wave = System.Text.Encoding.ASCII.GetString(wav, 8, 4);
            string fmt = System.Text.Encoding.ASCII.GetString(wav, 12, 4);
            string data = System.Text.Encoding.ASCII.GetString(wav, 36, 4);
            int channels = BitConverter.ToInt16(wav, 22);
            int rate = BitConverter.ToInt32(wav, 24);
            int bits = BitConverter.ToInt16(wav, 34);
            int dataSize = BitConverter.ToInt32(wav, 40);
            sb.AppendLine($"  {stats[i].Name}: {riff}/{wave}/{fmt}/{data} · " +
                          $"声道 {channels} · 采样率 {rate} · 位深 {bits} · data {dataSize} 字节 · " +
                          $"{(ok ? "有效" : "无效：" + reason)}");
        }

        sb.AppendLine();
        sb.AppendLine("── 确定性 ──");
        sb.AppendLine($"  构造期只合成一次并缓存：{stats.Count} 个缓冲区（不是每次播放现合成）");

        sb.AppendLine();
        sb.AppendLine($"── 开关默认值：{new MaggotConfig().SoundEnabled}（规格 §13 要求 true） ──");

        sb.AppendLine();
        sb.AppendLine("── 30ms 连点保护实测 ──");
        int before = _sound.PlayCount;
        bool first = _sound.PlaySquash();
        bool immediate = _sound.PlaySquash();          // 立刻再来一次，必须被守卫拦下
        Thread.Sleep(SoundService.RetriggerGuardMs + 30);
        bool afterGuard = _sound.PlaySquash();
        sb.AppendLine($"  第一次播放     : {first}");
        sb.AppendLine($"  30ms 内再播放  : {immediate}（期望 False = 被拦下）");
        sb.AppendLine($"  等待 {SoundService.RetriggerGuardMs + 30}ms 后再播: {afterGuard}（期望 True）");
        sb.AppendLine($"  PlayCount 变化 : {before} → {_sound.PlayCount}");
        sb.AppendLine($"  LastError      : {_sound.LastError ?? "(无)"}");

        string path = Path.Combine(AppPaths.DataDirectory, "sound-diagnostics.txt");
        try
        {
            AppPaths.EnsureDataDirectory();
            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            SafeWriteLine($"音效诊断写入失败: {ex.Message}");
        }

        SafeWriteLine(sb.ToString());
        SafeWriteLine($"音效诊断已写入: {path}");
    }

    private static int ExpectedDuration(string name) => name switch
    {
        "拍死" => SoundService.SquashMs,
        "苍蝇嗡鸣" => SoundService.BuzzMs,
        "孵化" => SoundService.HatchMs,
        "繁殖" => SoundService.BredMs,
        _ => 0,
    };

    // ------------------------------------------------------------------
    // --diag-hover：程序化驱动规格 §14 的驻留逻辑。
    //
    // 真实鼠标没法自动驱动（而且虫群层收不到鼠标消息），所以这里直接把合成光标位置
    // 和 dt 喂给 <see cref="HoverTracker"/>，逐条核对 §14.4 的验收标准，
    // 并检查气泡窗口的 Win32 样式位。跑完立刻退出。
    // ------------------------------------------------------------------

    private void RunHoverDiagnostics()
    {
        StringBuilder sb = new();
        sb.AppendLine("══════ 悬停查看任务诊断（规格 §14）══════");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (_overlay is null || _hoverBubble is null || _overlay.Model.Count == 0)
        {
            sb.AppendLine("[FAIL] 覆盖层 / 气泡 / 生物不足，无法诊断。");
            WriteHoverReport(sb);
            ExitApp();
            return;
        }

        // ① 气泡窗口的 Win32 样式（§14.1 / §14.3）
        int ex = View.WindowStyles.GetExStyle(_hoverBubble);
        bool noActivate = (ex & View.WindowStyles.WS_EX_NOACTIVATE) != 0;
        bool toolWindow = (ex & View.WindowStyles.WS_EX_TOOLWINDOW) != 0;
        bool transparent = (ex & View.WindowStyles.WS_EX_TRANSPARENT) != 0;

        sb.AppendLine();
        sb.AppendLine("── ① 气泡窗口样式（§14.1）──");
        sb.AppendLine($"  Win32 ExStyle      : 0x{ex:X8}");
        sb.AppendLine($"  WS_EX_NOACTIVATE   : {noActivate}   （不抢焦点）");
        sb.AppendLine($"  WS_EX_TOOLWINDOW   : {toolWindow}   （不进 Alt-Tab / 任务栏）");
        sb.AppendLine($"  WS_EX_TRANSPARENT  : {transparent}   （不挡下面程序的点击）");
        sb.AppendLine($"  WS_EX_LAYERED      : {(ex & View.WindowStyles.WS_EX_LAYERED) != 0}");
        sb.AppendLine($"  Topmost            : {_hoverBubble.Topmost}");
        sb.AppendLine($"  ShowInTaskbar      : {_hoverBubble.ShowInTaskbar}");
        sb.AppendLine($"  ShowActivated      : {_hoverBubble.ShowActivated}");
        sb.AppendLine($"  Focusable          : {_hoverBubble.Focusable}");

        // ② 挑两只生物做 A / B。诊断期间**暂停模拟**并把位置钉死 ——
        //    否则虫子一边爬一边测，「驻留 3 秒」这件事根本测不准。
        bool wasRunning = _state.Config.Running;
        _state.Config.Running = false;

        List<CreatureVisual> creatures = new(_overlay.Model.Creatures);
        CreatureVisual? a = null;
        foreach (CreatureVisual c in creatures)
        {
            TaskItem? t = Engine.FindById(c.TaskId);
            if (t is { Status: not TaskStatus.Done, Rot: >= 0.50f and < 0.78f }) { a = c; break; }
        }
        a ??= creatures[0];
        CreatureVisual? b = null;
        foreach (CreatureVisual c in creatures)
        {
            if (!ReferenceEquals(c, a)) { b = c; break; }
        }
        b ??= creatures[0];

        TaskItem taskA = Engine.FindById(a.TaskId)!;
        TaskItem taskB = Engine.FindById(b.TaskId)!;

        Point pa = a.Position;
        Point pb = b.Position;
        Point off = new(_overlay.Model.Bounds.Right - 4, _overlay.Model.Bounds.Bottom - 4);

        // 钉死的目标快照：只有 A 和 B 两只，位置不再变化。
        List<HoverTarget> fixedTargets = new()
        {
            new HoverTarget(a.TaskId, pa, a.Size),
            new HoverTarget(b.TaskId, pb, b.Size),
        };

        sb.AppendLine();
        sb.AppendLine("── ② 测试对象（诊断期间已暂停模拟，位置钉死）──");
        sb.AppendLine($"  A: 「{taskA.Title}」 位置 ({pa.X:0.0}, {pa.Y:0.0}) 尺寸 {a.Size:0.0} " +
                      $"阶段 {LifecycleEngine.StageOf(taskA).Label()} rot {taskA.Rot:0.00}");
        sb.AppendLine($"  B: 「{taskB.Title}」 位置 ({pb.X:0.0}, {pb.Y:0.0}) 尺寸 {b.Size:0.0} " +
                      $"阶段 {LifecycleEngine.StageOf(taskB).Label()} rot {taskB.Rot:0.00}");
        sb.AppendLine($"  命中半径系数 size × {HoverTracker.HitRadiusScale}（§14.1「至少 ~0.7 × size」）");

        _hoverBubble.Tracker.Reset();
        sb.AppendLine();
        sb.AppendLine("── ③ 驻留时序 ──");
        sb.AppendLine("  轮询语义：第一次发现某只目标的那一帧算 t=0，之后每 100ms 累加一次；");
        sb.AppendLine("           所以「停 N 秒」= N×10 + 1 次轮询（§14 是持续时间，不是计数）。");

        // 验收 1：停 2.9 秒 → 不弹（2.9s = 30 次轮询）
        StepDwell(_hoverBubble, fixedTargets, pa, 30, sb, "停 2.9 秒 → 不弹出（§14.4-1）", false, null);

        // 验收 2：停到 3.0 秒（第 31 次轮询）→ 弹出，且标题与那只虫的任务一致
        HoverPollResult shown = _hoverBubble.PollFixed(fixedTargets, pa, 0.1);
        string shownTitle = "(未弹出)";
        bool titleOk = false;
        if (_hoverBubble.IsVisible)
        {
            _hoverBubble.UpdateContent(taskA, DailyKeys.Today());
            PumpUntilBubbleLaidOut();
            shownTitle = taskA.Title;
            titleOk = string.Equals(shownTitle, taskA.Title, StringComparison.Ordinal)
                     && _hoverBubble.VisibleTaskId == a.TaskId;
        }
        bool showOk = shown.ShouldShow && _hoverBubble.IsVisible;
        sb.AppendLine($"  [{(showOk ? "PASS" : "FAIL")}] 停到 3.0 秒（第 31 次轮询）→ 弹出（§14.4-2）" +
                      $"  → ShouldShow={shown.ShouldShow} IsVisible={_hoverBubble.IsVisible} Dwell={_hoverBubble.Tracker.Dwell:0.00}s");
        sb.AppendLine($"  [{(titleOk ? "PASS" : "FAIL")}] 气泡标题与那只虫对应的任务一致，且绑定的是 A 的 id（§14.4-2）" +
                      $"  → 标题=\"{shownTitle}\" VisibleTaskId={(_hoverBubble.VisibleTaskId == a.TaskId ? "A" : _hoverBubble.VisibleTaskId ?? "(null)")}");
        sb.AppendLine($"  气泡布局尺寸      : {_hoverBubble.ActualWidth:0.#} × {_hoverBubble.ActualHeight:0.#}");
        Rect bounds = _hoverBubble.ScreenBounds;
        sb.AppendLine($"  气泡屏幕位置      : L={bounds.Left:0.#} T={bounds.Top:0.#} W={bounds.Width:0.#} H={bounds.Height:0.#}");
        Point screenA = new(_overlay.Left + pa.X, _overlay.Top + pa.Y);
        sb.AppendLine($"  气泡贴着光标      : 光标 (屏幕 {screenA.X:0.#},{screenA.Y:0.#}) → 气泡左上 " +
                      $"({bounds.Left:0.#},{bounds.Top:0.#})　偏移 ({bounds.Left - screenA.X:0.#},{bounds.Top - screenA.Y:0.#})px" +
                      $"（设计偏移 {HoverBubbleWindow.CursorGap:0}px，贴边时自动翻转）");

        // 内容字段（§14.2）
        sb.AppendLine();
        sb.AppendLine("── ④ 气泡内容字段（§14.2）──");
        sb.AppendLine($"  标题 : {taskA.Title}");
        sb.AppendLine($"  阶段 : {LifecycleEngine.StageOf(taskA).Label()}（{LifecycleEngine.StageOf(taskA).Code()}）");
        sb.AppendLine($"  腐烂 : {taskA.Rot * 100.0:0}%");
        sb.AppendLine($"  放置 : {DailyKeys.PlacedText(taskA.LastCaredAt, LifecycleEngine.NowMs())}");
        sb.AppendLine($"  每日 : {(string.IsNullOrEmpty(taskA.DailyTemplateId) && string.IsNullOrEmpty(taskA.DailyKey) ? "(普通任务，不显示该行)" : taskA.DailyKey)}");
        sb.AppendLine($"  每周 : {(string.IsNullOrEmpty(taskA.WeeklyTemplateId) && string.IsNullOrEmpty(taskA.WeeklyKey) ? "(普通任务，不显示该行)" : taskA.WeeklyKey)}");

        // 验收 3：从 A 直接划到 B（中间只给 1 秒）→ 只显示 B
        HoverPollResult toB = _hoverBubble.PollFixed(fixedTargets, pb, 1.0);
        sb.AppendLine();
        Check(!toB.ShouldShow && !_hoverBubble.IsVisible, true,
            "从 A 划到 B 后只过了 1.0 秒 → 不显示（驻留按个体归零）（§14.3）", sb,
            $"ShouldShow={toB.ShouldShow} Dwell={_hoverBubble.Tracker.Dwell:0.00}s");

        HoverPollResult bShown = toB;
        for (int i = 0; i < 4; i++) bShown = _hoverBubble.PollFixed(fixedTargets, pb, 0.1);
        Check(bShown.ShouldShow, false, "在 B 上共停 0.4 秒 → 仍不显示（§14.3）", sb,
            $"ShouldShow={bShown.ShouldShow} Dwell={_hoverBubble.Tracker.Dwell:0.00}s");
        // 从划到 B 那一帧算起共 31 次轮询 = 3.0 秒；再多给几次确保越过阈值。
        for (int i = 0; i < 26; i++) bShown = _hoverBubble.PollFixed(fixedTargets, pb, 0.1);
        _hoverBubble.UpdateContent(taskB, DailyKeys.Today());
        string visible = _hoverBubble.VisibleTaskId == b.TaskId ? "B"
            : _hoverBubble.VisibleTaskId == a.TaskId ? "A"
            : _hoverBubble.VisibleTaskId ?? "(null)";
        Check(bShown.ShouldShow && _hoverBubble.IsVisible && _hoverBubble.VisibleTaskId == b.TaskId, true,
            "在 B 上停满 3.1 秒 → 只显示 B（§14.4-3）", sb,
            $"Dwell={_hoverBubble.Tracker.Dwell:0.00}s VisibleTaskId={visible}");
        // 验收 4：移开 → 立刻收起
        HoverPollResult offResult = _hoverBubble.PollFixed(fixedTargets, off, 0.1);
        Check(!offResult.ShouldShow && !_hoverBubble.IsVisible, true,
            "移开后立刻收起（§14.4-4）", sb,
            $"ShouldShow={offResult.ShouldShow} IsVisible={_hoverBubble.IsVisible}");

        // 暂停时依然有效（§14.3）—— 诊断全程 running=false，这条本来就是在暂停态下跑的
        StepDwell(_hoverBubble, fixedTargets, pa, 31, sb,
            "暂停（running=false）时悬停依然有效（§14.3）", true, a.TaskId);
        _hoverBubble.Tracker.Reset();
        _hoverBubble.Hide();
        _state.Config.Running = wasRunning;

        sb.AppendLine();
        sb.AppendLine("── ⑤ 轮询机制 ──");
        sb.AppendLine($"  轮询方式        : GetCursorPos（Cursor.Position），无全局鼠标钩子、不订阅覆盖层鼠标事件");
        sb.AppendLine($"  轮询频率        : 每 {HoverTracker.PollIntervalSeconds * 1000:0} ms（§14.1 要求约 10–15Hz）");
        sb.AppendLine($"  触发驻留        : {HoverTracker.DwellSeconds:0.0} 秒");
        sb.AppendLine($"  命中半径        : size × {HoverTracker.HitRadiusScale}");

        WriteHoverReport(sb);
        ExitApp();
    }

    /// <summary>往诊断报告里追加一条 PASS/FAIL 断言。</summary>
    private static void Check(bool actual, bool expected, string what, StringBuilder sb, string detail)
    {
        bool ok = actual == expected;
        sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {what}  → {detail}");
    }

    /// <summary>按 100ms 步长把一个驻留过程喂给追踪器（位置钉死），并核对最终状态。</summary>
    private static void StepDwell(
        HoverBubbleWindow bubble,
        IReadOnlyList<HoverTarget> targets,
        Point cursor,
        int polls,
        StringBuilder sb,
        string what,
        bool expectShown,
        string? expectTaskId)
    {
        bubble.Tracker.Reset();
        bubble.Hide();

        HoverPollResult result = default;
        for (int i = 0; i < polls; i++)
        {
            result = bubble.PollFixed(targets, cursor, HoverTracker.PollIntervalSeconds);
        }

        bool ok = result.ShouldShow == expectShown;
        if (ok && expectTaskId is not null) ok = result.HoveredId == expectTaskId;

        sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {what}" +
                      $"  → {polls} 次轮询 · Dwell={bubble.Tracker.Dwell:0.00}s ShouldShow={result.ShouldShow}");
    }

    /// <summary>让刚 Show 出来的气泡跑完布局一帧，这样 ActualWidth/位置才可信。</summary>
    private static void PumpUntilBubbleLaidOut()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }
    private void WriteHoverReport(StringBuilder sb)
    {
        string path = Path.Combine(AppPaths.DataDirectory, "hover-diagnostics.txt");
        try
        {
            AppPaths.EnsureDataDirectory();
            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            SafeWriteLine($"悬停诊断写入失败: {ex.Message}");
        }

        SafeWriteLine(sb.ToString());
        SafeWriteLine($"悬停诊断已写入: {path}");
    }

    // ------------------------------------------------------------------
    // --diag-weekly：每周任务（规格 §15）的运行期诊断。
    //
    // 不需要鼠标：直接建模板、建逾期实例，然后把**真实可视树**里的
    // 「已逾期 N 周」标签数出来 —— 只在 VM 里断言不够，
    // §15.5 要求的是「界面上显示为『已逾期 N 周』」。
    // 诊断结束前会把引擎内容还原，绝不覆盖用户真实状态。
    // ------------------------------------------------------------------

    private void RunWeeklyDiagnostics()
    {
        // ① 先备份真实状态（诊断只在一份副本上折腾）
        List<TaskItem> backupTasks = Engine.Snapshot().ToList();
        List<DailyTemplate> backupDaily = Engine.DailyTemplates().ToList();
        List<WeeklyTemplate> backupWeekly = Engine.WeeklyTemplates().ToList();
        bool backupWeeklyEnabled = _state.Config.WeeklyEnabled;

        StringBuilder sb = new();
        sb.AppendLine("══════ 每周任务诊断（规格 §15）══════");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        string weekKey = WeeklyKeys.Today();
        int isoWeekday = WeeklyKeys.TodayWeekday();
        string prevWeekKey = WeeklyKeys.FromLocal(DateTime.Today.AddDays(-7));

        sb.AppendLine("── 周键与星期（§15.3）──");
        sb.AppendLine($"本周周键（本地日期）  : {weekKey}");
        sb.AppendLine($"今天 ISO 星期几       : {isoWeekday}（{WeeklyKeys.WeekdayLabel(isoWeekday)}）");
        sb.AppendLine($"上一周的周键          : {prevWeekKey}");
        sb.AppendLine($"ISOWeek.GetYear 对照  : DateTime.Year={DateTime.Today.Year} " +
                      $"ISOWeek.GetYear={System.Globalization.ISOWeek.GetYear(DateTime.Today)}");
        sb.AppendLine($"星期名对照            : " + string.Join(" · ",
            Enumerable.Range(1, 7).Select(i => $"{i}→{WeeklyKeys.WeekdayLabel(i)}")));
        sb.AppendLine($"weeklyEnabled         : {_state.Config.WeeklyEnabled}");

        // ② 造场景：今天该生成的模板 + 一个还没到那天的模板 + 一个停用的模板 + 一个上周的逾期实例
        Engine.ClearWeeklyTemplates();

        int laterWeekday = isoWeekday == 7 ? 6 : 7;   // 周日时退化成「迟到也补」，其余是「还没到那天」
        WeeklyTemplate? due = Engine.AddWeeklyTemplate("写周报", isoWeekday, string.Empty, LifecycleEngine.NowMs());
        WeeklyTemplate? later = Engine.AddWeeklyTemplate("周复盘", laterWeekday, string.Empty, LifecycleEngine.NowMs());
        WeeklyTemplate? disabled = Engine.AddWeeklyTemplate("已停用的模板", isoWeekday, string.Empty, LifecycleEngine.NowMs());
        if (disabled is not null) Engine.SetWeeklyTemplateEnabled(disabled.Id, false);

        sb.AppendLine();
        sb.AppendLine("── 场景 ──");
        sb.AppendLine($"新增模板「写周报」weekday={isoWeekday}（= 今天）→ 立刻生成本周实例：" +
                      $"{Engine.FindWeeklyInstance(due!.Id, weekKey) is not null}");
        sb.AppendLine($"新增模板「周复盘」weekday={laterWeekday}（今天 {isoWeekday} " +
                      $"{(isoWeekday < laterWeekday ? "< 该星期 → 不生成（§15.6-2）" : "≥ 该星期 → 迟到也补（§15.4）")}）" +
                      $"→ 本周实例：{Engine.FindWeeklyInstance(later!.Id, weekKey) is not null}");

        // 上周没写的那份：造一个上一周周键的未完成实例（规格 §15.5「上周的周报没写」必须一直挂着）
        TaskItem? overdue = Engine.AddTask(
            WeeklyKeys.InstanceTitle("写周报", prevWeekKey), string.Empty, LifecycleEngine.NowMs());
        if (overdue is not null)
        {
            overdue.WeeklyTemplateId = due.Id;
            overdue.WeeklyKey = prevWeekKey;
            overdue.Rot = 0.55f;                 // 已经是蛹
            overdue.Status = TaskStatus.Doing;
        }
        Engine.DrainEvents();

        OpenTaskList();
        SyncViews();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        if (_taskList is not null)
        {
            TaskListViewModel vm = _taskList.ViewModel;
            sb.AppendLine();
            sb.AppendLine("── 每周任务区（任务列表窗口）──");
            sb.AppendLine($"任务列表窗口      : IsVisible={_taskList.IsVisible} " +
                          $"尺寸 {_taskList.ActualWidth:0.#}×{_taskList.ActualHeight:0.#}");
            sb.AppendLine($"每周模板数        : {vm.WeeklyRows.Count}");
            sb.AppendLine($"每周区标题        : {vm.WeeklyHeaderText}");
            sb.AppendLine($"「还没有模板」提示是否显示: {vm.HasNoWeekly}");
            foreach (WeeklyTemplateRowViewModel row in vm.WeeklyRows)
            {
                sb.AppendLine($"  · 「{row.Title}」 weekday={row.Template.Weekday}({row.WeekdayLabel}) " +
                              $"enabled={row.Enabled} 本周状态 chip={row.ThisWeekChipText} " +
                              $"标记=\"{row.SummaryText}\" 逾期周数={row.OverdueWeeks}");
            }

            List<TaskRowViewModel> weeklyRows = vm.ActiveRows.Where(r => r.IsWeekly).ToList();
            sb.AppendLine($"任务列表里的每周实例行数: {weeklyRows.Count}");
            foreach (TaskRowViewModel row in weeklyRows)
            {
                sb.AppendLine($"  · 「{row.Title}」 rot={row.Rot:0.00} 逾期=\"{row.OverdueText}\" " +
                              $"MetaText=\"{row.MetaText}\"");
            }

            // 让逾期标记真的出现在可视树里（与 --diag-ui 同样的做法）
            List<TaskRowViewModel> ordered = vm.ActiveRows.OrderByDescending(r => r.IsOverdue).ToList();
            vm.ActiveRows.Clear();
            foreach (TaskRowViewModel r in ordered) vm.ActiveRows.Add(r);
            _taskList.Refresh();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            List<System.Windows.Controls.TextBlock> badges = new();
            CollectText(_taskList, badges);
            int weekBadges = badges.Count(t =>
                t.Text.StartsWith("已逾期", StringComparison.Ordinal) &&
                t.Text.EndsWith("周", StringComparison.Ordinal));
            int dayBadges = badges.Count(t =>
                t.Text.StartsWith("已逾期", StringComparison.Ordinal) &&
                t.Text.EndsWith("天", StringComparison.Ordinal));
            sb.AppendLine($"可视树里的「已逾期 N 周」标签数: {weekBadges}" +
                          $"　→ {(weekBadges > 0 ? "逾期周标记已渲染（PASS）" : "没有渲染出来（FAIL）")}");
            sb.AppendLine($"可视树里的「已逾期 N 天」标签数: {dayBadges}（每日任务区，本次未造场景）");

            // 视觉证据：把带 3 条每周模板的任务列表整窗渲染一张 PNG
            CaptureWindowProof(sb, "tasklist-weekly", _taskList);
        }

        // 悬停气泡上的「每周」行（规格 §15.5 要求逾期在气泡里也显示「已逾期 N 周」）——
        // 不需要鼠标：直接调 UpdateContent。
        sb.AppendLine();
        sb.AppendLine("── 悬停气泡（§14.2 / §15.5，直接喂任务，不需要鼠标）──");
        if (_hoverBubble is not null)
        {
            TaskItem? hoverProbe = overdue ?? Engine.Snapshot().FirstOrDefault();
            if (hoverProbe is not null)
            {
                _hoverBubble.UpdateContent(hoverProbe, DailyKeys.Today(), weekKey);
                sb.AppendLine($"  任务:「{hoverProbe.Title}」（{hoverProbe.WeeklyKey ?? "(非每周实例)"}）");
                sb.AppendLine($"  每日行: {_hoverBubble.DailyRowText}");
                sb.AppendLine($"  每周行: {_hoverBubble.WeeklyRowText}" +
                              $"　→ {(_hoverBubble.WeeklyRowText.StartsWith("已逾期", StringComparison.Ordinal) ? "已显示「已逾期 N 周」（PASS）" : "未显示逾期周数（FAIL）")}");

                // 拿一只普通任务做对照：每周行必须收起
                TaskItem? plain = backupTasks.FirstOrDefault(t => t.WeeklyKey is null && t.DailyKey is null);
                if (plain is not null)
                {
                    _hoverBubble.UpdateContent(plain, DailyKeys.Today(), weekKey);
                    sb.AppendLine($"  对照（普通任务）: 每周行 {_hoverBubble.WeeklyRowText}");
                }
            }
        }
        else
        {
            sb.AppendLine("  (悬停气泡不存在)");
        }

        // ③ 还原真实状态，避免诊断覆盖用户的 state.json
        Engine.ReplaceAll(backupTasks, _state.SquashedCount);
        Engine.ReplaceDailyTemplates(backupDaily);
        Engine.ReplaceWeeklyTemplates(backupWeekly);
        Engine.DrainEvents();
        _state.Config.WeeklyEnabled = backupWeeklyEnabled;

        sb.AppendLine();
        sb.AppendLine($"（诊断结束前已还原真实状态：任务 {Engine.Count}，每日模板 " +
                      $"{Engine.DailyTemplates().Count}，每周模板 {Engine.WeeklyTemplates().Count}）");

        string path = Path.Combine(AppPaths.DataDirectory, "weekly-diagnostics.txt");
        try
        {
            AppPaths.EnsureDataDirectory();
            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            SafeWriteLine($"每周诊断写入失败: {ex.Message}");
        }

        SafeWriteLine(sb.ToString());
        SafeWriteLine($"每周诊断已写入: {path}");
        ExitApp();
    }

    // ------------------------------------------------------------------
    // UI 诊断（--diag-ui）：跑 5 秒，把窗口/字体/穿透/渲染的实测事实写成报告后退出。
    // 用于交付验证，不参与正常启动路径。
    // ------------------------------------------------------------------

    private void StartUiDiagnostics()
    {        System.Text.StringBuilder report = new();
        DispatcherTimer probe = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(5) };
        probe.Tick += (_, _) =>
        {
            probe.Stop();

            report.AppendLine("══════ UI 运行期诊断 ══════");
            report.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine();

            report.AppendLine("── 窗口 ──");
            report.AppendLine($"主屏虚拟桌面: L={SystemParameters.VirtualScreenLeft:0.#} " +
                              $"T={SystemParameters.VirtualScreenTop:0.#} " +
                              $"W={SystemParameters.VirtualScreenWidth:0.#} " +
                              $"H={SystemParameters.VirtualScreenHeight:0.#}");
            report.AppendLine($"显示器数量: {System.Windows.Forms.Screen.AllScreens.Length}");
            report.AppendLine($"ClientAreaAnimation(系统动效开关): {SystemParameters.ClientAreaAnimation}");
            report.AppendLine($"ReduceMotion(实际生效): {ReduceMotion}");

            if (_overlay is not null)
            {
                report.AppendLine();
                report.AppendLine("── 虫群覆盖层 ──");
                report.AppendLine($"IsVisible      : {_overlay.IsVisible}");
                report.AppendLine($"Left/Top       : {_overlay.Left:0.#} / {_overlay.Top:0.#}");
                report.AppendLine($"Width/Height   : {_overlay.ActualWidth:0.#} / {_overlay.ActualHeight:0.#}");
                report.AppendLine($"WindowStyle    : {_overlay.WindowStyle}");
                report.AppendLine($"ResizeMode     : {_overlay.ResizeMode}");
                report.AppendLine($"AllowsTransparency: {_overlay.AllowsTransparency}");
                report.AppendLine($"Background     : {_overlay.Background}");
                report.AppendLine($"ShowInTaskbar  : {_overlay.ShowInTaskbar}");
                report.AppendLine($"Topmost        : {_overlay.Topmost}");

                int ex = View.WindowStyles.GetExStyle(_overlay);
                report.AppendLine($"Win32 ExStyle  : 0x{ex:X8}");
                report.AppendLine($"  WS_EX_LAYERED    : {(ex & View.WindowStyles.WS_EX_LAYERED) != 0}");
                report.AppendLine($"  WS_EX_TRANSPARENT: {(ex & View.WindowStyles.WS_EX_TRANSPARENT) != 0}  (穿透)");
                report.AppendLine($"  WS_EX_TOOLWINDOW : {(ex & View.WindowStyles.WS_EX_TOOLWINDOW) != 0}");
                report.AppendLine($"  WS_EX_NOACTIVATE : {(ex & View.WindowStyles.WS_EX_NOACTIVATE) != 0}");
                report.AppendLine($"IsClickThroughEffective(): {_overlay.IsClickThroughEffective()}");
                report.AppendLine($"配置 clickThrough        : {_state.Config.ClickThrough}");

                report.AppendLine();
                report.AppendLine("── 穿透开关运行时切换 ──");
                _overlay.ApplyClickThrough(false);
                int off = View.WindowStyles.GetExStyle(_overlay);
                report.AppendLine($"ApplyClickThrough(false) → WS_EX_TRANSPARENT={(off & View.WindowStyles.WS_EX_TRANSPARENT) != 0}");
                _overlay.ApplyClickThrough(true);
                int on = View.WindowStyles.GetExStyle(_overlay);
                report.AppendLine($"ApplyClickThrough(true)  → WS_EX_TRANSPARENT={(on & View.WindowStyles.WS_EX_TRANSPARENT) != 0}");
                report.AppendLine($"切换后仍保留 LAYERED/TOOLWINDOW/NOACTIVATE: " +
                                  $"{((on & View.WindowStyles.WS_EX_LAYERED) != 0)}/" +
                                  $"{((on & View.WindowStyles.WS_EX_TOOLWINDOW) != 0)}/" +
                                  $"{((on & View.WindowStyles.WS_EX_NOACTIVATE) != 0)}");

                report.AppendLine();
                report.AppendLine("── 渲染 ──");
                report.AppendLine($"虫群生物数: {_overlay.Model.Count}");
                report.AppendLine($"爆浆粒子非空: {!_overlay.Model.Splats.IsEmpty}");
                report.AppendLine($"最近一帧耗时: {_overlay.LastFrameMs:0.00} ms");
                report.AppendLine($"平均绘制耗时: {_overlay.AverageFrameMs:0.00} ms/帧（共 {_overlay.FrameCount} 帧）");
                report.AppendLine($"中位绘制耗时: {_overlay.MedianFrameMs:0.00} ms/帧  P95: {_overlay.P95FrameMs:0.00} ms/帧");
                report.AppendLine($"帧驱动: CompositionTarget.Rendering + DispatcherTimer({LifecycleEngine.IdleTickHz}Hz 兜底)");
            }

            report.AppendLine();
            report.AppendLine("── 控制面板 ──");
            if (_panel is not null)
            {
                report.AppendLine($"IsVisible: {_panel.IsVisible}  Left/Top: {_panel.Left:0.#}/{_panel.Top:0.#}  " +
                                  $"Actual: {_panel.ActualWidth:0.#}×{_panel.ActualHeight:0.#}");
                var warmup = _panel.FontWarmup;
                report.AppendLine($"FontWarmup 文本(中文): {warmup.Text}");
                report.AppendLine($"FontWarmup 解析字体: {warmup.FontFamily}");
                report.AppendLine($"FontWarmup 是否渲染出中文(宽度>0): {WarmupWidth(warmup):0.0}px");
                report.AppendLine($"控制面板按钮文案: {_panel.ViewModel.RunButtonText} / 状态 {_panel.ViewModel.StatusText}");
                report.AppendLine($"中文数字条: 卵={_panel.ViewModel.EggCount} 蛆={_panel.ViewModel.LarvaCount} " +
                                  $"蛹={_panel.ViewModel.PupaCount} 苍蝇={_panel.ViewModel.FlyCount}");
                report.AppendLine($"腐烂压力: 平均 {_panel.ViewModel.AverageRotText} · {_panel.ViewModel.TotalText}");
            }

            report.AppendLine();
            report.AppendLine("── 引擎 ──");
            report.AppendLine($"任务数: {Engine.Count}  苍蝇: {Engine.FlyCount}  拍死: {Engine.SquashedCount}");
            report.AppendLine($"阶段计数: {Engine.StageCounts()}");
            report.AppendLine($"运行中: {_state.Config.Running}  腐化倍速: {_state.Config.RotSpeed}");
            report.AppendLine($"状态文件: {AppPaths.StateFile} (存在 {File.Exists(AppPaths.StateFile)})");

            // v1.1 新增界面：每日任务模板 + 面板收起按钮 + 音效开关
            DiagnoseNewUi(report);

            report.AppendLine();
            report.Append(FontProbe.Describe());

            // 视觉证据：把虫群层分别合成到纯白 / 纯黑背景上，验证设计系统 §0 的硬约束
            // 「生物在纯白和纯黑背景上都清晰可辨」。
            CaptureSwarmProof(report);

            string path = Path.Combine(AppPaths.DataDirectory, "ui-diagnostics.txt");
            try
            {
                AppPaths.EnsureDataDirectory();
                File.WriteAllText(path, report.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                SafeWriteLine($"UI 诊断写入失败: {ex.Message}");
            }

            SafeWriteLine(report.ToString());
            SafeWriteLine($"UI 诊断已写入: {path}");

            ExitApp();
        };
        probe.Start();
    }

    /// <summary>
    /// v1.1 新增界面的运行期诊断：
    ///   · 控制面板标题行的「收起 / 任务列表」按钮（含热区尺寸）
    ///   · 「收起」→ 面板隐藏 → 托盘菜单文案同步（不脱钩）
    ///   · 每日任务模板列表（模板 → 今天的实例状态 → 逾期标记）
    ///   · 音效开关打开时立刻试听「拍死」（规格 §13）
    /// 全程用真实控件，但不需要鼠标：直接调 VM 的属性/命令。
    /// </summary>
    private void DiagnoseNewUi(StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("── v1.1 新增界面（每日任务 / 收起按钮 / 音效试听）──");

        // ---- 每日任务：建两个模板，其中一个制造「逾期」实例 ----
        Engine.ClearDailyTemplates();
        DailyTemplate? exercise = Engine.AddDailyTemplate("锻炼", string.Empty, LifecycleEngine.NowMs());
        DailyTemplate? review = Engine.AddDailyTemplate("每日复盘", string.Empty, LifecycleEngine.NowMs());
        string today = DailyKeys.Today();

        // 给「锻炼」造一个 2 天前的未完成实例（逾期 2 天）
        TaskItem? overdue = Engine.AddTask(
            DailyKeys.InstanceTitle("锻炼", DailyKeys.FromLocal(DateTime.Today.AddDays(-2))),
            string.Empty,
            LifecycleEngine.NowMs());
        if (overdue is not null)
        {
            overdue.DailyTemplateId = exercise?.Id;
            overdue.DailyKey = DailyKeys.FromLocal(DateTime.Today.AddDays(-2));
            overdue.Rot = 0.55f;   // 已经是蛹
            overdue.Status = TaskStatus.Doing;
        }

        // 给「每日复盘」造一个今天已完成的实例
        TaskItem? todayReview = Engine.AddTask(
            DailyKeys.InstanceTitle("每日复盘", today), string.Empty, LifecycleEngine.NowMs());
        if (todayReview is not null)
        {
            todayReview.DailyTemplateId = review?.Id;
            todayReview.DailyKey = today;
            Engine.Complete(todayReview.Id, LifecycleEngine.NowMs(), out _);
        }
        Engine.DrainEvents();

        report.AppendLine($"模板数            : {Engine.DailyTemplates().Count}");
        report.AppendLine($"今日日期键（本地）: {today}");

        OpenTaskList();
        SyncViews();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        if (_taskList is not null)
        {
            TaskListViewModel vm = _taskList.ViewModel;
            report.AppendLine($"任务列表窗口      : IsVisible={_taskList.IsVisible} " +
                              $"尺寸 {_taskList.ActualWidth:0.#}×{_taskList.ActualHeight:0.#}");
            report.AppendLine($"每日模板行数      : {vm.DailyRows.Count}（期望 2）");
            report.AppendLine($"每日区标题        : {vm.DailyHeaderText}");
            report.AppendLine($"「还没有模板」提示是否显示: {vm.HasNoDaily}");
            foreach (DailyTemplateRowViewModel row in vm.DailyRows)
            {
                report.AppendLine($"  · 「{row.Title}」 enabled={row.Enabled} " +
                                  $"今日状态 chip={row.TodayChipText} " +
                                  $"标记=\"{row.SummaryText}\" 逾期天数={row.OverdueDays}");
            }

            TaskRowViewModel? overdueRow = vm.ActiveRows.FirstOrDefault(r => r.IsOverdue);
            report.AppendLine($"任务列表里的逾期标记: " +
                (overdueRow is null
                    ? "(没有逾期行)"
                    : $"「{overdueRow.Title}」→ 「{overdueRow.OverdueText}」 MetaText=\"{overdueRow.MetaText}\""));
            report.AppendLine($"逾期行数          : {vm.ActiveRows.Count(r => r.IsOverdue)} / {vm.ActiveRows.Count}");

            // 让逾期标记真的出现在可视树里，并截一张图作为证据 ——
            // 只在 VM 里断言不够，规格 §12.1 要求的是「界面上给逾期实例打标记」。
            if (overdueRow is not null)
            {
                List<TaskRowViewModel> ordered = vm.ActiveRows
                    .OrderByDescending(r => r.IsOverdue)
                    .ToList();
                vm.ActiveRows.Clear();
                foreach (TaskRowViewModel r in ordered) vm.ActiveRows.Add(r);
                _taskList.Refresh();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

                List<System.Windows.Controls.TextBlock> badges = new();
                CollectText(_taskList, badges);
                int badgeCount = badges.Count(t => t.Text.StartsWith("已逾期", StringComparison.Ordinal));
                report.AppendLine($"可视树里的「已逾期 N 天」标签数: {badgeCount}" +
                                  $"　→ {(badgeCount > 0 ? "逾期标记已渲染（PASS）" : "没有渲染出来（FAIL）")}");
                // 逾期标记是红色描边的小胶囊，把它所在的整行截出来存档
                CaptureElementProof(report, "overdue-row", _taskList,
                    badges.FirstOrDefault(t => t.Text.StartsWith("已逾期", StringComparison.Ordinal)));
            }
        }

        // ---- 控制面板：标题行按钮 + 收起/唤回 + 托盘文案同步 ----
        if (_panel is not null)
        {
            report.AppendLine();
            // 面板宽度 348 是设计值；这里量出每个内容块的真实右边界，
            // 用来判断「有没有被裁掉」——只看截图会误判。
            report.AppendLine($"面板宽度          : {_panel.ActualWidth:0.#}（设计 348）");
            List<System.Windows.FrameworkElement> wide = new();
            CollectWidest(_panel, wide);
            foreach (System.Windows.FrameworkElement fe in wide)
            {
                double right = fe.TranslatePoint(new Point(fe.ActualWidth, 0), _panel).X;
                string label = fe switch
                {
                    System.Windows.Controls.TextBlock tb =>
                        "TextBlock:" + (tb.Text.Length > 14 ? tb.Text[..14] + "…" : tb.Text),
                    System.Windows.Controls.Button b => "Button:" + b.Content,
                    _ => fe.GetType().Name,
                };
                report.AppendLine($"  超宽元素 {label} 宽 {fe.ActualWidth:0.#} 右边界 {right:0.#}" +
                                  $"{(right > _panel.ActualWidth + 0.5 ? "  ← 溢出面板" : string.Empty)}");
            }
            report.AppendLine($"面板可见          : {_panel.IsVisible}");
            report.AppendLine($"托盘菜单文案(前)  : 「{_tray.PanelMenuText}」");

            // 面板上的「收起」走的是 host.TogglePanel()，与托盘菜单同一条路径。
            TogglePanel();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            report.AppendLine($"点「收起」后面板   : IsVisible={_panel.IsVisible}");
            report.AppendLine($"托盘菜单文案(后)  : 「{_tray.PanelMenuText}」" +
                              $"　→ {(string.Equals(_tray.PanelMenuText, "显示控制面板", StringComparison.Ordinal) ? "已同步（PASS）" : "未同步（FAIL）")}");

            TogglePanel();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            report.AppendLine($"再唤回面板        : IsVisible={_panel.IsVisible}");
            report.AppendLine($"托盘菜单文案(回)  : 「{_tray.PanelMenuText}」" +
                              $"　→ {(string.Equals(_tray.PanelMenuText, "隐藏控制面板", StringComparison.Ordinal) ? "已同步（PASS）" : "未同步（FAIL）")}");

            // 标题行里两个按钮的真实热区
            List<System.Windows.Controls.Button> headerButtons = new();
            CollectButtons(_panel, headerButtons);
            foreach (System.Windows.Controls.Button btn in headerButtons)
            {
                Point origin = btn.TranslatePoint(new Point(0, 0), _panel);
                report.AppendLine($"标题行按钮「{btn.Content}」: 热区 {btn.ActualWidth:0.#}×{btn.ActualHeight:0.#} " +
                                  $"@({origin.X:0.#},{origin.Y:0.#}) Focusable={btn.Focusable} " +
                                  $"FocusVisualStyle={(btn.FocusVisualStyle is null ? "无（FAIL）" : "有（PASS）")}");
            }

            // 音效开关：打开时必须立刻试听「拍死」（规格 §13）
            int before = _sound.PlayCount;
            _panel.ViewModel.SoundEnabled = false;
            int afterOff = _sound.PlayCount;
            _panel.ViewModel.SoundEnabled = true;
            int afterOn = _sound.PlayCount;
            report.AppendLine();
            report.AppendLine($"音效开关 关 → 开   : PlayCount {before} → {afterOff} → {afterOn} " +
                              $"（打开时立刻试听「拍死」：{(afterOn - afterOff > 0 ? "PASS" : "FAIL")}）");
            report.AppendLine($"音效 Enabled 状态  : {_sound.Enabled}");
            report.AppendLine($"每日开关/概览      : DailyEnabled={_panel.ViewModel.DailyEnabled} " +
                              $"「{_panel.ViewModel.DailySummaryText}」");
            // 「收起」按钮不是拖动手柄的一部分：它在独立列里，不在手柄的可视子树内。
            System.Windows.Controls.Button? hideBtn = headerButtons
                .FirstOrDefault(b => string.Equals(b.Content as string, "收起", StringComparison.Ordinal));
            System.Windows.Controls.Button? listBtn = headerButtons
                .FirstOrDefault(b => string.Equals(b.Content as string, "任务列表", StringComparison.Ordinal));
            if (hideBtn is not null && listBtn is not null)
            {
                double headerRight = listBtn.TranslatePoint(new Point(listBtn.ActualWidth, 0), _panel).X;
                report.AppendLine($"标题行占用右边界  : {headerRight:0.#} / 面板宽 {_panel.ActualWidth:0.#} " +
                                  $"→ {(headerRight <= _panel.ActualWidth ? "未被裁切（PASS）" : "溢出（FAIL）")}");
            }
        }

        // 视觉证据：把控制面板与任务列表（含每日任务区）各渲染一张 PNG。
        CaptureWindowProof(report, "panel", _panel);
        CaptureWindowProof(report, "tasklist", _taskList);
    }

    /// <summary>递归收集某个可视子树里的全部 TextBlock。</summary>
    private static void CollectText(System.Windows.DependencyObject root,
        List<System.Windows.Controls.TextBlock> sink)
    {
        if (root is System.Windows.Controls.TextBlock tb) sink.Add(tb);
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            CollectText(System.Windows.Media.VisualTreeHelper.GetChild(root, i), sink);
        }
    }

    /// <summary>把可视树里的某个元素连同它的祖先容器渲染出来（用于核对小控件）。</summary>
    private static void CaptureElementProof(StringBuilder report, string name, Window window,
        System.Windows.FrameworkElement? element)
    {
        if (element is null) return;
        try
        {
            // 往上找到那个「阶段色竖条 + 内容」的整行 Border，截整行更有说服力
            System.Windows.DependencyObject? node = element;
            System.Windows.FrameworkElement row = element;
            for (int i = 0; i < 12 && node is not null; i++)
            {
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
                if (node is System.Windows.Controls.Border b) row = b;
            }

            double dip = System.Windows.Media.VisualTreeHelper.GetDpi(row).PixelsPerDip;
            var origin = row.TranslatePoint(new Point(0, 0), window);
            int w = (int)Math.Ceiling(row.ActualWidth * dip);
            int h = (int)Math.Ceiling(row.ActualHeight * dip);
            if (w <= 1 || h <= 1) return;

            var rtb = new RenderTargetBitmap(w, h, 96.0 * dip, 96.0 * dip, PixelFormats.Pbgra32);
            rtb.Render(row);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            string path = Path.Combine(AppPaths.DataDirectory, $"{name}.png");
            AppPaths.EnsureDataDirectory();
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(fs);
            }
            report.AppendLine($"已写出 {name} 截图: {path} ({w}×{h} 物理像素，" +
                              $"源自 ({origin.X + window.Left:0.#},{origin.Y + window.Top:0.#}))");
        }
        catch (Exception ex)
        {
            report.AppendLine($"{name} 截图生成失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把某个窗口渲染成 PNG 存档，用于人眼核对新加的那几块界面。
    ///
    /// ⚠️ 必须按窗口实际的 DPI 比例渲染：系统在 125% 缩放时，
    /// 316 个设备无关单位 = 395 个物理像素，用 96dpi 截会丢掉右边 20% 的画面。
    /// </summary>
    private static void CaptureWindowProof(StringBuilder report, string name, Window? window)
    {
        if (window is null) return;
        try
        {
            var root = (FrameworkElement)window.Content;
            double dip = System.Windows.Media.VisualTreeHelper.GetDpi(root).PixelsPerDip;
            int w = (int)Math.Ceiling(Math.Max(1.0, root.ActualWidth) * dip);
            int h = (int)Math.Ceiling(Math.Max(1.0, root.ActualHeight) * dip);
            if (w <= 1 || h <= 1) return;

            var rtb = new RenderTargetBitmap(w, h, 96.0 * dip, 96.0 * dip, PixelFormats.Pbgra32);
            rtb.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            string path = Path.Combine(AppPaths.DataDirectory, $"{name}-v11.png");
            AppPaths.EnsureDataDirectory();
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(fs);
            }

            report.AppendLine($"已写出 {name} 截图: {path} ({w}×{h} 物理像素)");
            report.AppendLine($"  {name} 可视树尺寸: window={window.ActualWidth:0.#}×{window.ActualHeight:0.#} " +
                              $"root={root.ActualWidth:0.#}×{root.ActualHeight:0.#} dpi={dip:0.##}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"{name} 截图生成失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>找出右边界超出面板宽度的元素（诊断裁切用）。</summary>
    private static void CollectWidest(System.Windows.DependencyObject root,
        List<System.Windows.FrameworkElement> sink)
    {
        if (root is System.Windows.FrameworkElement fe && fe.ActualWidth > 240)
        {
            sink.Add(fe);
        }
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            CollectWidest(System.Windows.Media.VisualTreeHelper.GetChild(root, i), sink);
        }
    }

    /// <summary>递归收集某个可视子树里的全部按钮。</summary>
    private static void CollectButtons(System.Windows.DependencyObject root,
        List<System.Windows.Controls.Button> sink)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            System.Windows.DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Button b) sink.Add(b);
            CollectButtons(child, sink);
        }
    }

    /// <summary>
    /// 用 RenderTargetBitmap 把虫群层渲染出来，并分别合成到纯白 / 纯黑背景上存档。
    /// 这是设计系统 §0（深色描边 + 柔和外阴影 + 中间调身体）唯一的客观证据：
    /// 只有真的渲染出来、并在两种极端背景上都可辨识，才算过关。
    /// </summary>
    private void CaptureSwarmProof(System.Text.StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("── 视觉证据（虫群层合成到纯白 / 纯黑）──");

        if (_overlay is null)
        {
            report.AppendLine("覆盖层不存在，跳过。");
            return;
        }

        try
        {
            // 制造几坨爆浆，连同生物一起进画面
            Stage[] stages = { Stage.Egg, Stage.Larva, Stage.Pupa, Stage.Fly };
            int spawned = 0;
            foreach (CreatureVisual c in _overlay.Model.Creatures)
            {
                if (spawned >= 4) break;
                _overlay.Model.Splats.Spawn(
                    new Point(c.Position.X + 70, c.Position.Y + 70),
                    stages[spawned % stages.Length],
                    _clock.Elapsed.TotalSeconds);
                spawned++;
            }
            _overlay.Model.Splats.Update(_clock.Elapsed.TotalSeconds, 0.016);

            var root = (FrameworkElement)_overlay.Content;
            double dip = System.Windows.Media.VisualTreeHelper.GetDpi(root).PixelsPerDip;
            int w = (int)Math.Ceiling(Math.Max(1.0, root.ActualWidth > 0 ? root.ActualWidth : _overlay.Width) * dip);
            int h = (int)Math.Ceiling(Math.Max(1.0, root.ActualHeight > 0 ? root.ActualHeight : _overlay.Height) * dip);

            // 只截一块有虫子的区域，便于人眼检查
            var rtb = new RenderTargetBitmap(w, h, 96.0 * dip, 96.0 * dip, PixelFormats.Pbgra32);
            rtb.Render(root);
            report.AppendLine($"虫群层渲染: {rtb.PixelWidth}×{rtb.PixelHeight} 物理像素（dpi 比例 {dip:0.##}），" +
                              $"生物 {_overlay.Model.Count} 只");

            double lightRatio = 0;
            string whitePath = Path.Combine(AppPaths.DataDirectory, "swarm-on-white.png");
            string blackPath = Path.Combine(AppPaths.DataDirectory, "swarm-on-black.png");

            lightRatio = Composite(rtb, System.Windows.Media.Colors.White, whitePath);
            Composite(rtb, System.Windows.Media.Colors.Black, blackPath);

            report.AppendLine($"已写出: {whitePath}");
            report.AppendLine($"已写出: {blackPath}");
            report.AppendLine($"白色背景上非白像素占比: {lightRatio:P3}（>0 说明生物确实画在画面上了）");
        }
        catch (Exception ex)
        {
            report.AppendLine($"视觉证据生成失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把虫群层叠到指定底色上并存成 PNG；返回非底色像素占比。</summary>
    private static double Composite(RenderTargetBitmap overlay, System.Windows.Media.Color background, string path)
    {
        int w = overlay.PixelWidth;
        int h = overlay.PixelHeight;

        DrawingVisual dv = new();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, w, h));
            dc.DrawImage(overlay, new Rect(0, 0, w, h));
        }

        var composed = new RenderTargetBitmap(w, h, overlay.DpiX, overlay.DpiY, PixelFormats.Pbgra32);
        composed.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(composed));
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            encoder.Save(fs);
        }

        // 统计与底色明显不同的像素
        int stride = w * 4;
        byte[] pixels = new byte[stride * h];
        composed.CopyPixels(pixels, stride, 0);

        int different = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            int db = Math.Abs(b - background.B);
            int dg = Math.Abs(g - background.G);
            int dr = Math.Abs(r - background.R);
            if (db + dg + dr > 24) different++;
        }

        return different / (double)(w * h);
    }

    /// <summary>
    /// 用控件实际生效的字体排版它的文本，量出宽度。
    /// 宽度 &gt; 0 说明中文确实被某个回退字体渲染出来了（不是豆腐块、也不是空）。
    /// </summary>
    private static double WarmupWidth(System.Windows.Controls.TextBlock block)
    {
        try
        {
            FormattedText ft = new(
                block.Text,
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
                FlowDirection.LeftToRight,
                new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
                block.FontSize,
                System.Windows.Media.Brushes.White,
                96.0);
            return ft.WidthIncludingTrailingWhitespace;
        }
        catch (Exception)
        {
            return -1.0;
        }
    }
}
