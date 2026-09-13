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
    private readonly SwarmRenderer _renderer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _lastFrameAt;
    private bool _reduceMotion;
    private bool _clickThrough = true;

    public OverlayWindow()
    {
        InitializeComponent();

        _renderer = new SwarmRenderer(_model);
        RootCanvas.Children.Add(_renderer);

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;

        // 分辨率 / 显示器数量变化时重新铺满虚拟桌面
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>虫群模型（App 负责同步任务与推进）。</summary>
    public SwarmModel Model => _model;

    /// <summary>点击（非穿透状态下）拍到某只虫子时触发。</summary>
    public event Action<string, Stage>? CreatureSquashed;

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

    /// <summary>运行时开关鼠标穿透。</summary>
    public void ApplyClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        try
        {
            WindowStyles.ApplyOverlayStyles(this, clickThrough);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MaggotTask] 切换穿透失败: {ex.Message}");
        }
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
    // 关闭穿透后：点一下就拍
    // ------------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_clickThrough) return;

        Point p = e.GetPosition(this);
        CreatureVisual? hit = _model.HitTest(p);
        if (hit is null) return;

        Stage stage = hit.Stage;
        string id = hit.TaskId;
        CreatureSquashed?.Invoke(id, stage);
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        CompositionTarget.Rendering -= OnRendering;
        base.OnClosed(e);
    }

    // ==================================================================
    // 渲染器：三个 DrawingVisual 图层（阴影 / 本体 / 爆浆）
    //
    // 阴影必须单独成层，否则模糊会把本体也糊掉。
    // 模糊效果实例是全类共享且 Freeze 的，避免每帧 new 出一个新的 Effect
    // （每帧重建 Effect 会让 WPF 反复重编译 shader）。
    // ==================================================================
    private sealed class SwarmRenderer : FrameworkElement
    {
        private readonly SwarmModel _model;
        private readonly VisualCollection _layers;
        private readonly DrawingVisual _shadowLayer = new();
        private readonly DrawingVisual _bodyLayer = new();
        private readonly DrawingVisual _splatLayer = new();

        public SwarmRenderer(SwarmModel model)
        {
            _model = model;
            _layers = new VisualCollection(this)
            {
                _shadowLayer,
                _bodyLayer,
                _splatLayer,
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

            using (DrawingContext dc = _splatLayer.RenderOpen())
            {
                _model.Splats.Draw(dc, now, ReduceMotion);
            }
        }
    }
}
