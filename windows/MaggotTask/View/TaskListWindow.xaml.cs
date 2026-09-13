using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MaggotTask.Core;

namespace MaggotTask.View;

/// <summary>
/// 任务列表窗口（设计系统 §8）。
/// 每行：阶段色竖条 + 标题 + [阶段 chip][rot 进度条][剩余时间] + 我去做了/完成/删除。
/// 「战果」分组折叠，最多 50 条。
/// </summary>
public partial class TaskListWindow : Window
{
    private readonly IAppHost _host;
    private readonly TaskListViewModel _vm;
    private readonly DispatcherTimer _refreshTimer;

    private bool _dragging;
    private Point _dragOrigin;
    private double _dragLeft;
    private double _dragTop;

    /// <summary>新增每周模板时选中的 ISO 星期几（1=周一 … 7=周日）。</summary>
    private int _weeklyWeekday;

    public TaskListWindow(IAppHost host)
    {
        _host = host;
        InitializeComponent();

        _vm = new TaskListViewModel(host);
        DataContext = _vm;

        // 默认选中「今天」：用户新建一条每周任务时，最可能就是想在今天看到它出现。
        SetWeeklyWeekday(WeeklyKeys.TodayWeekday());

        SourceInitialized += (_, _) =>
        {
            WindowStyles.ApplyToolWindowStyle(this);
            FitToWorkArea();
        };
        Loaded += (_, _) => FitToWorkArea();
        LocationChanged += (_, _) => FitToWorkArea();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _refreshTimer.Tick += (_, _) => _vm.Refresh();
        _refreshTimer.Start();

        _vm.Refresh();
        NewTaskBox.Focus();
        Closed += (_, _) => _refreshTimer.Stop();
    }

    /// <summary>列表视图模型（App 刷新用）。</summary>
    public TaskListViewModel ViewModel => _vm;

    /// <summary>按当前引擎状态重建列表。</summary>
    public void Refresh() => _vm.Refresh();

    // ------------------------------------------------------------------
    // 每日任务模板（规格 §12.4）
    // ------------------------------------------------------------------

    private void OnAddDailyClick(object sender, RoutedEventArgs e) => CreateDailyFromBox();

    private void OnNewDailyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CreateDailyFromBox();
            e.Handled = true;
        }
    }

    private void CreateDailyFromBox()
    {
        string title = NewDailyBox.Text.Trim();
        if (title.Length == 0)
        {
            NewDailyBox.Focus();
            return;
        }

        DailyTemplate? created = _host.AddDailyTemplate(title, out int spawnedToday);
        if (created is not null)
        {
            NewDailyBox.Clear();
            // 规格 §12.4：新增模板要**立刻**看得见今天这只虫。
            ConfirmDialog.Info(this, "模板已建立",
                spawnedToday > 0
                    ? $"「{created.Title}」每天会生成一只新虫。今天这只已经放进缸里了。"
                    : $"「{created.Title}」每天会生成一只新虫。今天这只已经在缸里（或任务数已达上限 {_host.Config.MaxTasks}）。");
        }

        NewDailyBox.Focus();
        _vm.Refresh();
    }

    // ------------------------------------------------------------------
    // 每周任务模板（规格 §15.5 → §12.4）
    // ------------------------------------------------------------------

    /// <summary>当前选中的星期几（诊断用）。</summary>
    public int SelectedWeekday => _weeklyWeekday;

    /// <summary>当前「星期几」按钮上的文案（诊断用）。</summary>
    public string WeekdayButtonText => WeekdayButton.Content as string ?? string.Empty;

    /// <summary>设置要新增的星期几（1=周一 … 7=周日）；按钮文案同步。</summary>
    public void SetWeeklyWeekday(int isoWeekday)
    {
        _weeklyWeekday = Math.Clamp(isoWeekday, 1, 7);
        string label = WeeklyKeys.WeekdayLabel(_weeklyWeekday);
        WeekdayButton.Content = _weeklyWeekday == WeeklyKeys.TodayWeekday()
            ? $"{label} · 今天"
            : label;
    }

    private void OnPickWeekdayClick(object sender, RoutedEventArgs e)
    {
        // 点一下往后挪一天，周日之后回到周一（7 个选项，循环一周）。
        SetWeeklyWeekday(_weeklyWeekday >= 7 ? 1 : _weeklyWeekday + 1);
    }

    private void OnAddWeeklyClick(object sender, RoutedEventArgs e) => CreateWeeklyFromBox();

    private void OnNewWeeklyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CreateWeeklyFromBox();
            e.Handled = true;
        }
    }

    private void CreateWeeklyFromBox()
    {
        string title = NewWeeklyBox.Text.Trim();
        if (title.Length == 0)
        {
            NewWeeklyBox.Focus();
            return;
        }

        WeeklyTemplate? created = _host.AddWeeklyTemplate(title, _weeklyWeekday, out int spawnedThisWeek);
        if (created is not null)
        {
            NewWeeklyBox.Clear();
            // 规格 §15.6-1：今天已经到达该星期就要**立刻**看得见这只虫；§15.6-2：还没到那天就不生成。
            ConfirmDialog.Info(this, "模板已建立",
                spawnedThisWeek > 0
                    ? $"「{created.Title}」每周{WeeklyKeys.WeekdayLabel(_weeklyWeekday)}会生成一只新虫。本周这只已经放进缸里了。"
                    : $"「{created.Title}」每周{WeeklyKeys.WeekdayLabel(_weeklyWeekday)}会生成一只新虫。" +
                      $"还没到那一天（或者本周这只已经在缸里 / 任务数已达上限 {_host.Config.MaxTasks}），到那天会自动出现。");
        }

        NewWeeklyBox.Focus();
        _vm.Refresh();
    }

    // ------------------------------------------------------------------
    // 拖动窗口
    // ------------------------------------------------------------------

    private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 空白的标题行才是拖动手柄；点在标题文字上也算（标题行没有按钮）。
        _dragging = true;
        _dragOrigin = PointToScreen(e.GetPosition(this));
        _dragLeft = Left;
        _dragTop = Top;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        Point now = PointToScreen(e.GetPosition(this));
        double dx = now.X - _dragOrigin.X;
        double dy = now.Y - _dragOrigin.Y;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;

        Left = _dragLeft + dx;
        Top = _dragTop + dy;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        Mouse.Capture(null);
    }

    // ------------------------------------------------------------------
    // 新建任务
    // ------------------------------------------------------------------

    private void OnCreateTaskClick(object sender, RoutedEventArgs e) => CreateFromBox();

    private void OnNewTaskKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CreateFromBox();
            e.Handled = true;
        }
    }

    private void CreateFromBox()
    {
        string title = NewTaskBox.Text.Trim();
        if (title.Length == 0)
        {
            // 空标题不建虫，只给一次注意力提示。
            NewTaskBox.Focus();
            return;
        }

        if (_host.NewTask(title))
        {
            NewTaskBox.Clear();
        }
        else
        {
            ConfirmDialog.Info(this, "已经装不下了",
                $"任务数已达上限 {_host.Config.MaxTasks} 个。先拍死一些，或者清空重来。");
        }

        NewTaskBox.Focus();
        _vm.Refresh();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    // ------------------------------------------------------------------
    // 窗口适配 + Esc 收起
    // ------------------------------------------------------------------

    /// <summary>
    /// 把窗口压回工作区内。任务多时内容会把窗口撑得很高（或被拖到屏幕下方），
    /// 底部的「关闭」一旦落到屏幕外，用户就再也收不起这个窗口了。
    /// 拖动过程中也随时钳制，保证「收起」永远可见。
    /// </summary>
    private void FitToWorkArea()
    {
        double maxH = SystemParameters.WorkArea.Height - 24;
        if (MaxHeight != maxH) MaxHeight = maxH;
        if (Height > maxH) Height = maxH;

        double maxY = SystemParameters.WorkArea.Bottom - ActualHeight - 4;
        if (Top > maxY) Top = Math.Max(SystemParameters.WorkArea.Top, maxY);
        if (Top < SystemParameters.WorkArea.Top) Top = SystemParameters.WorkArea.Top;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Esc 收起 —— 与悬浮面板的交互习惯一致。
        // 注意输入框聚焦时 IME 可能吃掉 Escape 上屏取消，那种情况这里收不到，无碍。
        if (e.Key == Key.Escape && !e.Handled)
        {
            Hide();
            e.Handled = true;
        }
    }
}
