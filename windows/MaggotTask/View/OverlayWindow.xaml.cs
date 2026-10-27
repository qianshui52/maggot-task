using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MaggotTask.Core;
using MaggotTask.Render;

namespace MaggotTask.View;

/// <summary>
/// 全屏透明穿透的虫群覆盖层。
///
/// · 覆盖整个虚拟桌面（SystemParameters.VirtualScreen*），支持多显示器
/// · WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
/// · 关闭穿透后可以「拍」苍蝇（命中即拍死）
/// · 绘制走 DrawingVisual + CompositionTarget.Rendering，不使用 Shape 元素
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly SwarmModel _model = new();
    private readonly KillBurstField _bursts = new();
    private readonly SwarmRenderer _renderer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _lastFrameAt;
    private bool _reduceMotion;
    private bool _clickThrough = true;

    /// <summary>
    /// 用户在面板上设定的"穿透"意图。
    /// 实际是否穿透 = 这个意图 且 没有因为光标悬到虫子上而临时接管。
    /// </summary>
    private bool _manualClickThrough = true;

    /// <summary>当前是否因为"光标正压在虫子上"而临时接管了点击。</summary>
    private bool _autoCapturing;

    private ToolDef _tool = ToolCatalog.Swatter;
    private double _lastAttackAt = double.NegativeInfinity;

    /// <summary>本次出手累计命中数（诊断用）。</summary>
    public int TotalHits { get; private set; }

    /// <summary>因冷却被忽略的点击次数（诊断用）。</summary>
    public int CooldownSkips { get; private set; }

    /// <summary>空挥次数（点下去但附近没有目标，诊断用）。</summary>
    public int MissClicks { get; private set; }

    /// <summary>
    /// 窗口实际收到的左键按下次数（诊断用）。
    /// 与 <see cref="MissClicks"/> 的区别：这个数在"是否穿透"判断**之前**就加一，
    /// 所以 ClicksSeen = 0 表示鼠标事件根本没到窗口（命中/穿透问题），
    /// 而 ClicksSeen &gt; 0 且 MissClicks = 0 表示事件到了却被提前 return 掉了。
    /// </summary>
    public int ClicksSeen { get; private set; }

    /// <summary>内部穿透标志（诊断用，与 Win32 样式位对照）。</summary>
    public bool ClickThroughField => _clickThrough;

    /// <summary>
    /// Win32 层收到的 WM_LBUTTONDOWN 次数（诊断用）。
    /// 与 <see cref="ClicksSeen"/> 对照即可判断事件丢在哪一层：
    ///   WmClicks = 0            → 窗口根本没收到消息（被遮挡 / 样式 / 尺寸）
    ///   WmClicks &gt; ClicksSeen  → 消息到了，但 WPF 命中测试没把它路由成鼠标事件
    /// </summary>
    public int WmClicks { get; private set; }

    private const int WM_LBUTTONDOWN = 0x0201;

    public OverlayWindow()
    {
        InitializeComponent();

        _renderer = new SwarmRenderer(_model, _bursts);
        RootCanvas.Children.Add(_renderer);

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;

        // 分辨率 / 显示器数量变化时重新铺满虚拟桌面
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>虫群模型（App 负责同步任务与推进）。</summary>
    public SwarmModel Model => _model;

    /// <summary>消除特效场（诊断用）。</summary>
    public KillBurstField Bursts => _bursts;

    /// <summary>
    /// 一次出手打中的全部生物（可能 0 个，也可能一次一片）。
    /// App 负责记账 + 把对应任务标记完成。
    /// </summary>
    public event Action<IReadOnlyList<KillHit>>? CreaturesSquashed;

    /// <summary>
    /// 每个渲染帧触发一次：(秒级时间, dt)。App 用它驱动唯一的模拟循环，
    /// 保证「虫群动」和「腐烂推进」用的是同一个 dt。
    /// </summary>
    public event Action<double, double>? Frame;

    /// <summary>降低动效。</summary>
    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            _reduceMotion = value;
            _renderer.ReduceMotion = value;
        }
    }

    /// <summary>最近一帧的绘制耗时（毫秒），诊断用。</summary>
    public double LastFrameMs { get; private set; }

    /// <summary>累计帧数与累计绘制耗时，用于算稳定的平均帧耗。</summary>
    public long FrameCount { get; private set; }
    public double TotalRenderMs { get; private set; }

    /// <summary>最近 600 帧的绘制耗时，用于取中位数（比平均值更抗抖动）。</summary>
    private readonly Queue<double> _frameSamples = new(600);

    /// <summary>
    /// 相邻两帧之间的真实间隔（毫秒）。
    ///
    /// 这才是用户感知到的流畅度 —— <see cref="LastFrameMs"/> 只量到"构建绘制指令"的耗时，
    /// 真正的光栅化与 Effect 求值发生在 WPF 渲染线程上，不在这个区间里。
    /// 所以旧版报告里"中位 2ms/帧"和肉眼卡顿可以同时成立。
    /// </summary>
    private readonly Queue<double> _intervalSamples = new(900);

    private int _sinceIntervalStats;

    /// <summary>帧间隔中位数（毫秒）。</summary>
    public double MedianFrameIntervalMs { get; private set; }

    /// <summary>帧间隔 P95（毫秒），代表卡顿上限。</summary>
    public double P95FrameIntervalMs { get; private set; }

    /// <summary>最差一帧的间隔（毫秒）。</summary>
    public double WorstIntervalMs { get; private set; }

    /// <summary>实际达到的帧率（由帧间隔中位数换算）。</summary>
    public double AchievedFps => MedianFrameIntervalMs > 0 ? 1000.0 / MedianFrameIntervalMs : 0;

    /// <summary>清空帧统计，便于从一个干净窗口开始测量。</summary>
    public void ResetFrameStats()
    {
        _intervalSamples.Clear();
        _frameSamples.Clear();
        FrameCount = 0;
        TotalRenderMs = 0;
        MedianFrameIntervalMs = 0;
        P95FrameIntervalMs = 0;
        WorstIntervalMs = 0;
    }

    private void RecomputeIntervalStats()
    {
        if (_intervalSamples.Count == 0) return;
        double[] a = _intervalSamples.ToArray();
        Array.Sort(a);
        MedianFrameIntervalMs = a[a.Length / 2];
        P95FrameIntervalMs = a[Math.Min(a.Length - 1, (int)(a.Length * 0.95))];
        WorstIntervalMs = a[a.Length - 1];
    }

    /// <summary>平均绘制耗时（毫秒/帧）；没跑够帧时返回 0。</summary>
    public double AverageFrameMs => FrameCount == 0 ? 0 : TotalRenderMs / FrameCount;

    /// <summary>绘制耗时中位数（毫秒/帧）—— 比平均值更能代表稳态成本。</summary>
    public double MedianFrameMs
    {
        get
        {
            if (_frameSamples.Count == 0) return 0;
            double[] a = _frameSamples.ToArray();
            Array.Sort(a);
            return a[a.Length / 2];
        }
    }

    /// <summary>绘制耗时 P95（毫秒/帧）—— 代表卡顿上限。</summary>
    public double P95FrameMs
    {
        get
        {
            if (_frameSamples.Count == 0) return 0;
            double[] a = _frameSamples.ToArray();
            Array.Sort(a);
            return a[Math.Min(a.Length - 1, (int)(a.Length * 0.95))];
        }
    }

    // ------------------------------------------------------------------
    // 生命周期
    // ------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateBounds();
        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            WindowStyles.ApplyOverlayStyles(this, _clickThrough);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MaggotTask] 应用覆盖层扩展样式失败: {ex.Message}");
        }

        // 诊断：在 Win32 层统计左键按下，判断事件丢在消息层还是 WPF 命中测试层。
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProcHook);
        }
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_LBUTTONDOWN) WmClicks++;
        return IntPtr.Zero;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateBounds));
    }

    /// <summary>覆盖整个虚拟桌面。</summary>
    public void UpdateBounds()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = Math.Max(1.0, SystemParameters.VirtualScreenWidth);
        Height = Math.Max(1.0, SystemParameters.VirtualScreenHeight);

        _model.Bounds = new Rect(0, 0, Width, Height);
    }

    /// <summary>运行时设置"穿透"意图（面板开关）。会先清掉自动接管状态。</summary>
    public void ApplyClickThrough(bool clickThrough)
    {
        _manualClickThrough = clickThrough;
        _autoCapturing = false;
        ApplyEffectiveClickThrough();
    }

    /// <summary>
    /// 按「用户意图 + 自动接管」算出实际穿透状态，变化时才动 Win32 样式位。
    ///
    /// 为什么需要自动接管：穿透开着时窗口根本不参与命中测试，**收不到鼠标事件**，
    /// 光标也轮不到我们设置（由下层窗口决定），所以"悬停变苍蝇拍、点击拍死"全都不成立。
    /// 但让用户先手动关掉穿透又太隐蔽 —— 于是改成：光标压到虫子身上时临时撤销穿透，
    /// 离开后立刻恢复。中间态只有几十毫秒，用户感知就是"靠近就能拍"。
    /// </summary>
    private void ApplyEffectiveClickThrough()
    {
        bool desired = _manualClickThrough && !_autoCapturing;
        if (desired == _clickThrough) return;

        _clickThrough = desired;
        try
        {
            WindowStyles.ApplyOverlayStyles(this, desired);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MaggotTask] 切换穿透失败: {ex.Message}");
        }
        UpdateCursor();
    }

    /// <summary>
    /// 每帧调用：光标是否压在虫子上。
    ///
    /// 用**两档半径**做滞回（进入 1.7、离开 2.2）：否则光标停在判定边缘时
    /// 会一帧接管一帧放行，光标形状和点击归属跟着抖。
    /// </summary>
    /// <returns>当前是否处于自动接管状态。</returns>
    public bool UpdateCapture(Point cursorLocal)
    {
        // 用户已经手动关掉穿透 → 全程接管，不需要再做局部判断
        if (!_manualClickThrough)
        {
            if (_autoCapturing)
            {
                _autoCapturing = false;
                ApplyEffectiveClickThrough();
            }
            return false;
        }

        double scale = _autoCapturing ? 2.2 : 1.7;
        bool over = _model.HitTestAny(cursorLocal, scale);

        if (over != _autoCapturing)
        {
            _autoCapturing = over;
            ApplyEffectiveClickThrough();
        }
        return _autoCapturing;
    }

    /// <summary>面板上的穿透意图（诊断用）。</summary>
    public bool ManualClickThrough => _manualClickThrough;

    /// <summary>是否正因悬停而临时接管（诊断用）。</summary>
    public bool AutoCapturing => _autoCapturing;

    /// <summary>
    /// 换装备。光标与范围立即生效；冷却按新工具重新计算（不会卡着旧冷却）。
    /// </summary>
    public void ApplyTool(ToolDef tool)
    {
        _tool = tool ?? ToolCatalog.Swatter;
        _lastAttackAt = double.NegativeInfinity;
        UpdateCursor();
    }

    /// <summary>当前装备（诊断用）。</summary>
    public ToolDef CurrentTool => _tool;

    /// <summary>
    /// 只有"能吃点击"时才换工具光标 —— 穿透状态下窗口根本不接收命中测试，
    /// 光标应当保持系统箭头，否则用户会以为桌面上的东西都能点。
    /// </summary>
    private void UpdateCursor()
    {
        if (_clickThrough)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        Cursor = CursorFactory.ForTool(_tool.Id) ?? Cursors.Arrow;
    }

    /// <summary>诊断：当前是否真的处于穿透状态（读 Win32 样式位）。</summary>
    public bool IsClickThroughEffective()
    {
        try
        {
            return WindowStyles.IsClickThrough(this);
        }
        catch
        {
            return _clickThrough;
        }
    }

    // ------------------------------------------------------------------
    // 渲染循环（由 CompositionTarget.Rendering 驱动）
    // ------------------------------------------------------------------

    private void OnRendering(object? sender, EventArgs e)
    {
        long stamp = _clock.ElapsedTicks;
        double now = _clock.Elapsed.TotalSeconds;

        // 原始帧间隔（未 clamp）—— 用它测真实帧率；clamp 过的 dt 只用于物理推进。
        double rawIntervalMs = (stamp - _lastFrameAt) * 1000.0 / Stopwatch.Frequency;
        double dt = (stamp - _lastFrameAt) / (double)Stopwatch.Frequency;
        _lastFrameAt = stamp;

        if (dt <= 0) return;
        // 单帧最多推进 MAX_DT（0.5s），防止休眠/卡顿之后跳变。
        if (dt > LifecycleEngine.MaxDt) dt = LifecycleEngine.MaxDt;

        long wallStart = Stopwatch.GetTimestamp();

        // 引擎推进交给 App（它内部按 TICK_HZ 做固定步长），
        // 虫群位置按真实帧间隔推进，最后绘制。
        Frame?.Invoke(now, dt);
        _model.Update(now, dt, _reduceMotion);
        _bursts.Update(now, dt);
        _renderer.Redraw(now);

        LastFrameMs = (Stopwatch.GetTimestamp() - wallStart) * 1000.0 / Stopwatch.Frequency;
        FrameCount++;
        TotalRenderMs += LastFrameMs;

        if (_frameSamples.Count >= 600) _frameSamples.Dequeue();
        _frameSamples.Enqueue(LastFrameMs);

        if (_intervalSamples.Count >= 900) _intervalSamples.Dequeue();
        _intervalSamples.Enqueue(rawIntervalMs);
        // 排序有成本，但重算间隔必须够短 —— 否则低帧率下（每帧几百毫秒）
        // 8 秒里根本攒不到 30 帧，统计会一直是 0，读数就成了假的。
        if (++_sinceIntervalStats >= 5)
        {
            _sinceIntervalStats = 0;
            RecomputeIntervalStats();
        }
    }

    // ------------------------------------------------------------------
    // 关闭穿透后：点一下出手
    // ------------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        ClicksSeen++;
        base.OnMouseLeftButtonDown(e);
        if (_clickThrough) return;

        AttackAt(e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>
    /// 在指定点出手一次，返回本次实际命中的数量（0 = 空挥或被冷却拦下）。
    ///
    /// 鼠标事件与诊断（--diag-kill）走的是**同一条**路径 —— 这样"人点得中"和
    /// "测试点得中"不会出现两套逻辑。Win32 命中层不在这里验证（那是窗口样式的事），
    /// 这里验证的是「几何判定 → 移除 → 特效 → 通知 App 记账」。
    /// </summary>
    public int AttackAt(Point p)
    {
        double now = _clock.Elapsed.TotalSeconds;

        // 冷却：同一个工具两次出手之间必须间隔够 —— 否则范围武器可以连续点击刷屏。
        if (_tool.CooldownSeconds > 0 && now - _lastAttackAt < _tool.CooldownSeconds)
        {
            CooldownSkips++;
            return 0;
        }
        _lastAttackAt = now;

        List<CreatureVisual> hits = _model.CollectHits(_tool, p);

        if (hits.Count == 0)
        {
            MissClicks++;
            return 0;
        }

        var payload = new List<KillHit>(hits.Count);
        foreach (CreatureVisual c in hits)
        {
            payload.Add(new KillHit(c.TaskId, c.Stage, c.Position));
        }

        // 先从模型移除，视觉上"立刻消失"，再由特效接管
        foreach (CreatureVisual c in hits)
        {
            _model.RemoveAt(c.TaskId);
        }

        // 单点用小光环，范围武器按实际覆盖半径放大
        double ringRadius = _tool.Shape switch
        {
            ToolShape.Circle => _tool.Radius,
            ToolShape.Cross => _tool.Width * 2.2,
            ToolShape.Beam => 64.0,
            _ => 34.0,
        };

        foreach (KillHit hit in payload)
        {
            Stage stage = hit.Stage;
            int reward = ToolCatalog.RewardFor(stage);
            _bursts.Spawn(hit.At, stage, reward, now, ringRadius);
        }

        TotalHits += payload.Count;
        CreaturesSquashed?.Invoke(payload);
        return payload.Count;
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        CompositionTarget.Rendering -= OnRendering;
        base.OnClosed(e);
    }

    // ==================================================================
    // 渲染器：四个 DrawingVisual 图层（阴影 / 本体 / 消除特效 / 光环）
    //
    // 阴影必须单独成层，否则模糊会把本体也糊掉。
    // 模糊效果实例是全类共享且 Freeze 的，避免每帧 new 出一个新的 Effect
    // （每帧重建 Effect 会让 WPF 反复重编译 shader）。
    // ==================================================================
    private sealed class SwarmRenderer : FrameworkElement
    {
        private readonly SwarmModel _model;
        private readonly KillBurstField _bursts;
        private readonly VisualCollection _layers;
        private readonly DrawingVisual _shadowLayer = new();
        private readonly DrawingVisual _bodyLayer = new();
        private readonly DrawingVisual _burstLayer = new();

        public SwarmRenderer(SwarmModel model, KillBurstField bursts)
        {
            _model = model;
            _bursts = bursts;
            _layers = new VisualCollection(this)
            {
                _shadowLayer,
                _bodyLayer,
                _burstLayer,
            };

            // 软件渲染下 WPF 的图像缩放默认走 Fant（高质量重采样），把一张小图缩到
            // 几十像素宽会非常贵 —— 实测 140 只生物的阴影层因此要花掉 ~250ms/帧。
            // 阴影本来就是个模糊色块，双线性（LowQuality）肉眼看不出差别，但快一个数量级。
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

            // 只有旧路径才挂全屏模糊。SoftGradient 模式下阴影自带柔边，
            // 挂上 Effect 纯粹是浪费 —— 而且是软件渲染下最贵的一笔开销。
            if (CreaturePainter.Shadow == CreaturePainter.ShadowMode.BlurLayer)
            {
                _shadowLayer.Effect = CreaturePainter.SharedShadowEffect;
            }
        }

        public bool ReduceMotion { get; set; }

        protected override int VisualChildrenCount => _layers.Count;

        protected override Visual GetVisualChild(int index) => _layers[index];

        public void Redraw(double now)
        {
            using (DrawingContext dc = _shadowLayer.RenderOpen())
            {
                foreach (CreatureVisual c in _model.Creatures)
                {
                    CreaturePainter.DrawShadow(dc, c, now);
                }
            }

            using (DrawingContext dc = _bodyLayer.RenderOpen())
            {
                foreach (CreatureVisual c in _model.Creatures)
                {
                    CreaturePainter.DrawBody(dc, c, now, ReduceMotion);
                }
            }

            using (DrawingContext dc = _burstLayer.RenderOpen())
            {
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                _bursts.Draw(dc, now, ReduceMotion, dpi);
            }
        }
    }
}

/// <summary>一次出手命中的一只生物（位置是命中瞬间的位置）。</summary>
public readonly record struct KillHit(string TaskId, Stage Stage, Point At);
