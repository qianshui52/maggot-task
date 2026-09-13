using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MaggotTask.View;

/// <summary>
/// 控制面板（设计系统 §7）。
/// 布局自顶向下：标题行 → 数字条 → 腐烂压力条 → 启动/暂停 → 腐化倍速 → 次级行动 → 开关行。
/// 拖动手柄在标题行整条；最小触控目标 44×44；清空需二次确认。
/// </summary>
public partial class ControlPanelWindow : Window
{
    private readonly IAppHost _host;
    private readonly ControlPanelViewModel _vm;
    private readonly DispatcherTimer _refreshTimer;

    private bool _dragging;
    private Point _dragOrigin;

    public ControlPanelWindow(IAppHost host, double? savedLeft, double? savedTop)
    {
        _host = host;
        InitializeComponent();
        ThemeFontsWarmup();

        _vm = new ControlPanelViewModel(host);
        DataContext = _vm;

        SourceInitialized += (_, _) =>
        {
            WindowStyles.ApplyToolWindowStyle(this);
            RestorePosition(savedLeft, savedTop);
        };

        // 面板本身不跑模拟：只以 500ms 节拍把引擎状态拉过来显示。
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _refreshTimer.Tick += (_, _) => _vm.Refresh();
        _refreshTimer.Start();

        _vm.Refresh();
        _vm.SetStatusLine("已同步");

        // 拖动结束 / 位置变化时持久化
        LocationChanged += (_, _) => PositionChanged?.Invoke(Left, Top);
        Closed += (_, _) => _refreshTimer.Stop();

        // 「收起」按钮 / 托盘菜单都会改面板可见性 —— 每次显隐都通知宿主同步托盘文案，
        // 保证托盘那个「显示/隐藏控制面板」开关永远不会和实际状态脱钩。
        IsVisibleChanged += (_, _) => _host.NotifyPanelVisibilityChanged();
    }

    /// <summary>面板位置变化（用于持久化）。</summary>
    public event Action<double, double>? PositionChanged;

    public ControlPanelViewModel ViewModel => _vm;

    private static void ThemeFontsWarmup()
    {
        // 触发一次字体解析，确保 pack URI 指向的资源字体真的被加载
        // （资源缺失时 WPF 会静默回退，这里留下可诊断的痕迹）。
        _ = Render.ThemeFonts.Display.FamilyNames.Count;
    }

    private void RestorePosition(double? left, double? top)
    {
        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenTop = SystemParameters.VirtualScreenTop;
        double screenW = SystemParameters.VirtualScreenWidth;
        double screenH = SystemParameters.VirtualScreenHeight;

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 460;

        double x = left ?? (screenLeft + screenW - w - 32);
        double y = top ?? (screenTop + 72);

        // 屏幕布局变了也要保证面板落在可见区域内
        x = Math.Clamp(x, screenLeft, Math.Max(screenLeft, screenLeft + screenW - w));
        y = Math.Clamp(y, screenTop, Math.Max(screenTop, screenTop + screenH - h));

        Left = x;
        Top = y;
    }

    // ------------------------------------------------------------------
    // 拖动：只有左边的标题文字是手柄。
    // 收起 / 任务列表两个按钮在右侧独立列里，不在拖动手柄的可视子树内，
    // 所以点它们不会触发 OnDragHandleMouseDown（也永远不会被当成拖动）。
    // ------------------------------------------------------------------

    private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _dragging = true;
        _dragOrigin = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    /// <summary>「收起」：隐藏面板。托盘菜单的「显示/隐藏控制面板」文案随之同步（见 TrayService）。</summary>
    private void OnHidePanelClick(object sender, RoutedEventArgs e)
    {
        _dragging = false;
        Mouse.Capture(null);
        PositionChanged?.Invoke(Left, Top);
        _host.TogglePanel();     // 面板当前可见 → 变成隐藏；并同步托盘文案
        e.Handled = true;
    }

    /// <summary>「任务列表」：打开完整任务控制台。</summary>
    private void OnOpenTaskListClick(object sender, RoutedEventArgs e)
    {
        _host.OpenTaskList();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        Point now = e.GetPosition(this);
        Vector delta = now - _dragOrigin;

        // 只有真正移动了才当成拖动，避免「点一下标题」把面板弹走
        if (Math.Abs(delta.X) < 0.5 && Math.Abs(delta.Y) < 0.5) return;

        Left += delta.X;
        Top += delta.Y;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        Mouse.Capture(null);
        PositionChanged?.Invoke(Left, Top);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关闭控制面板 ≠ 退出应用（模拟继续跑，托盘图标仍在）。
        PositionChanged?.Invoke(Left, Top);
        base.OnClosing(e);
    }
}
