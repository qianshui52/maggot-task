using System.Windows;
using System.Windows.Interop;
using MaggotTask.Core;

namespace MaggotTask.View;

/// <summary>
/// 规格 §14 的悬停信息气泡。
///
/// 它是一个**独立的小窗口**，不是覆盖层里的控件 —— 覆盖层不可能收到鼠标消息，
/// 但气泡必须能贴光标移动，而且不能抢焦点、不能挡下面的点击。
///
/// 窗口样式（§14.1 / §14.2）：
///   WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT
///   ShowActivated=False + Focusable=False + IsHitTestVisible=False
///   Topmost，不进 Alt-Tab
/// </summary>
public partial class HoverBubbleWindow : Window
{
    /// <summary>气泡相对光标的偏移（px）。</summary>
    public const double CursorGap = 18.0;

    private readonly HoverTracker _tracker = new();
    private readonly Func<IReadOnlyList<HoverTarget>> _targets;
    private readonly Func<Point> _cursorProvider;
    private readonly Func<double> _clock;

    private double _lastTickSeconds;
    private double _nextPollSeconds;
    private string? _visibleTaskId;

    public HoverBubbleWindow(
        Func<IReadOnlyList<HoverTarget>> targets,
        Func<Point> cursorProvider,
        Func<double> clock)
    {
        _targets = targets;
        _cursorProvider = cursorProvider;
        _clock = clock;

        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            WindowStyles.ApplyHoverBubbleStyle(this);
            _lastTickSeconds = _clock();
            _nextPollSeconds = _lastTickSeconds;
        };
    }

    /// <summary>悬停追踪器（--diag-hover 直接驱动它）。</summary>
    public HoverTracker Tracker => _tracker;

    /// <summary>
    /// 覆盖层左上角在屏幕上的 DIP 坐标。气泡收到的是**覆盖层本地坐标**，
    /// 要摆到屏幕上必须先加回这个原点。
    ///
    /// 旧实现用 <c>PointToScreen</c> 把"覆盖层本地坐标"当成"气泡本地坐标"来换算，
    /// 气泡一移动就会把自己当前的位置再叠一次 —— 于是在 125% 缩放下越飘越远。
    /// </summary>
    public Point OverlayOrigin { get; set; }

    /// <summary>气泡当前展示的任务 id（null = 没显示）。</summary>
    public string? VisibleTaskId => _visibleTaskId;

    /// <summary>气泡里「每日」行当前文案 + 是否显示（诊断用，不需要鼠标）。</summary>
    public string DailyRowText =>
        $"{DailyText.Text}（{(DailyLabel.Visibility == Visibility.Visible ? "显示" : "隐藏")}）";

    /// <summary>气泡里「每周」行当前文案 + 是否显示（诊断用，不需要鼠标）。</summary>
    public string WeeklyRowText =>
        $"{WeeklyText.Text}（{(WeeklyLabel.Visibility == Visibility.Visible ? "显示" : "隐藏")}）";

    /// <summary>气泡在屏幕上的实际矩形（诊断用）。</summary>
    public Rect ScreenBounds
    {
        get
        {
            try
            {
                Point topLeft = PointToScreen(new Point(0, 0));
                return new Rect(topLeft, new Size(ActualWidth, ActualHeight));
            }
            catch
            {
                return Rect.Empty;
            }
        }
    }

    /// <summary>
    /// 主循环每帧调用一次；内部按 <see cref="HoverTracker.PollIntervalSeconds"/>
    /// 限流到约 10Hz 才真正轮询一次（规格 §14.1）。
    /// </summary>
    public void Tick()
    {
        double now = _clock();
        if (now < _nextPollSeconds) return;

        double dt = now - _lastTickSeconds;
        _lastTickSeconds = now;
        _nextPollSeconds = now + HoverTracker.PollIntervalSeconds;

        // dt 只会用来累加驻留时间；被卡顿拉长也无所谓（驻留判定本来就按真实时间）。
        PollOnce(dt);
    }

    /// <summary>立即按给定的 dt 轮询一次（诊断/自测用，不受限流影响）。</summary>
    public HoverPollResult PollOnce(double dt)
    {
        if (!IsVisible)
        {
            _tracker.Reset();
            ApplyResult(default);
            return default;
        }

        Point cursor = _cursorProvider();
        IReadOnlyList<HoverTarget> targets = _targets();
        HoverPollResult result = _tracker.Poll(targets, cursor, dt);
        ApplyResult(result, cursor);
        return result;
    }

    /// <summary>直接按给定的光标位置轮询一次（目标位置取当帧实时值）。</summary>
    public HoverPollResult PollAt(Point cursor, double dt)
    {
        IReadOnlyList<HoverTarget> targets = _targets();
        HoverPollResult result = _tracker.Poll(targets, cursor, dt);
        ApplyResult(result, cursor);
        return result;
    }

    /// <summary>
    /// 用**给定的一批固定目标位置**轮询一次。
    /// 诊断时生物还在动，只有把位置钉死才能验证「驻留 3 秒」这件事本身。
    /// </summary>
    public HoverPollResult PollFixed(IReadOnlyList<HoverTarget> targets, Point cursor, double dt)
    {
        HoverPollResult result = _tracker.Poll(targets, cursor, dt);
        ApplyResult(result, cursor);
        return result;
    }

    private void ApplyResult(HoverPollResult result, Point cursor = default)
    {
        if (!result.ShouldShow)
        {
            _visibleTaskId = null;
            if (IsVisible) Hide();
            return;
        }

        _visibleTaskId = result.HoveredId;
        Show();
        FollowCursor(cursor);
    }

    /// <summary>把气泡摆到光标旁边（每 100ms 更新一次，规格 §14.3「跟随」策略）。</summary>
    private void FollowCursor(Point overlayLocalCursor)
    {
        try
        {
            var screen = new Point(
                OverlayOrigin.X + overlayLocalCursor.X,
                OverlayOrigin.Y + overlayLocalCursor.Y);
            Rect work = SystemParameters.WorkArea;

            double gap = CursorGap;
            double w = ActualWidth > 0 ? ActualWidth : 200;
            double h = ActualHeight > 0 ? ActualHeight : 90;

            double x = screen.X + gap;
            double y = screen.Y + gap;

            if (x + w > work.Right) x = screen.X - gap - w;
            if (y + h > work.Bottom) y = screen.Y - gap - h;

            x = Math.Max(work.Left, Math.Min(x, Math.Max(work.Left, work.Right - w)));
            y = Math.Max(work.Top, Math.Min(y, Math.Max(work.Top, work.Bottom - h)));

            Left = x;
            Top = y;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MaggotTask] 气泡定位失败: {ex.Message}");
        }
    }

    /// <summary>按任务内容填充气泡（规格 §14.2）。</summary>
    public void UpdateContent(TaskItem task, string todayKey, string? weekKey = null)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(task.Title) ? "未命名任务" : task.Title;

        Stage stage = LifecycleEngine.StageOf(task);
        StageText.Text = $"{stage.Label()}（{stage.Code()}）";
        RotText.Text = $"{task.Rot * 100.0:0}%";

        long now = LifecycleEngine.NowMs();
        PlacedValueText.Text = DailyKeys.PlacedText(task.LastCaredAt, now);

        // 「每日」这一行只在每日实例上显示（规格 §14.2）
        bool isDaily = !string.IsNullOrEmpty(task.DailyTemplateId) || !string.IsNullOrEmpty(task.DailyKey);
        if (isDaily)
        {
            string overdue = DailyKeys.OverdueText(task.DailyKey, task.Status, todayKey);
            string status = task.Status == TaskStatus.Done
                ? "已完成"
                : (overdue.Length > 0 ? overdue : "进行中");
            DailyLabel.Visibility = Visibility.Visible;
            DailyText.Visibility = Visibility.Visible;
            DailyText.Text = status;
        }
        else
        {
            DailyLabel.Visibility = Visibility.Collapsed;
            DailyText.Visibility = Visibility.Collapsed;
            DailyText.Text = string.Empty;
        }

        // 「每周」行同理（规格 §15.5：逾期显示为「已逾期 N 周」）
        bool isWeekly = !string.IsNullOrEmpty(task.WeeklyTemplateId) || !string.IsNullOrEmpty(task.WeeklyKey);
        if (isWeekly)
        {
            string weekKeyValue = weekKey ?? WeeklyKeys.Today();
            string overdue = WeeklyKeys.OverdueText(task.WeeklyKey, task.Status, weekKeyValue);
            string status = task.Status == TaskStatus.Done
                ? "已完成"
                : (overdue.Length > 0 ? overdue : "本周");
            WeeklyLabel.Visibility = Visibility.Visible;
            WeeklyText.Visibility = Visibility.Visible;
            WeeklyText.Text = status;
        }
        else
        {
            WeeklyLabel.Visibility = Visibility.Collapsed;
            WeeklyText.Visibility = Visibility.Collapsed;
            WeeklyText.Text = string.Empty;
        }
    }
}
