using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MaggotTask.Core;
using MaggotTask.Render;

namespace MaggotTask.View;

/// <summary>
/// 悬停名牌：鼠标停在虫子上 0.18 秒就浮出来，显示「任务名 · 阶段 · +奖励」。
///
/// 它与规格 §14 的详情气泡是**两个不同的东西**：
///   · 名牌：快（0.18s）、只有一行、跟着光标走 —— 击杀模式的"准星反馈"
///   · 气泡：慢（3s）、有腐烂度/每日每周 —— 阅读用的详情卡
/// 两者同时出现会互相遮挡，所以 App 在气泡弹出时会主动收起名牌。
///
/// 窗口样式与气泡一致：NOACTIVATE | TOOLWINDOW | TRANSPARENT，不抢焦点、不吃点击。
/// </summary>
public partial class NameTagWindow : Window
{
    /// <summary>相对光标的偏移。</summary>
    public const double CursorGap = 14.0;

    private string? _taskId;
    private Stage _stage = Stage.Egg;

    public NameTagWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            try
            {
                WindowStyles.ApplyHoverBubbleStyle(this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MaggotTask] 名牌窗口样式失败: {ex.Message}");
            }
        };
    }

    /// <summary>当前展示的任务 id（null = 空）。</summary>
    public string? TaskId => _taskId;

    /// <summary>当前展示的阶段。</summary>
    public Stage Stage => _stage;

    /// <summary>填充内容；内容没变时不做任何重建（避免每帧刷新布局）。</summary>
    public void UpdateContent(TaskItem task, Stage stage, int reward)
    {
        _stage = stage;

        string title = string.IsNullOrWhiteSpace(task.Title) ? "未命名任务" : task.Title;
        if (!string.Equals(TitleText.Text, title, StringComparison.Ordinal))
        {
            TitleText.Text = title;
        }

        string stageLabel = $"{stage.Label()} · {stage.Code()}";
        if (!string.Equals(StageText.Text, stageLabel, StringComparison.Ordinal))
        {
            StageText.Text = stageLabel;
            StageDot.Background = BrushFor(stage);
        }

        string rewardText = reward > 0 ? "+" + reward : "—";
        if (!string.Equals(RewardText.Text, rewardText, StringComparison.Ordinal))
        {
            RewardText.Text = rewardText;
        }

        _taskId = task.Id;
    }

    /// <summary>显示（已显示则只移动）。</summary>
    public void ShowAt(Point screenDip)
    {
        if (!IsVisible) Show();
        MoveTo(screenDip);
    }

    /// <summary>收起（已收起则什么都不做）。</summary>
    public void HideTag()
    {
        _taskId = null;
        if (IsVisible) Hide();
    }

    /// <summary>跟随光标，并夹在虚拟桌面内。</summary>
    private void MoveTo(Point screenDip)
    {
        try
        {
            double w = ActualWidth > 0 ? ActualWidth : 180;
            double h = ActualHeight > 0 ? ActualHeight : 30;

            double x = screenDip.X + CursorGap;
            double y = screenDip.Y + CursorGap;

            double left = SystemParameters.VirtualScreenLeft;
            double top = SystemParameters.VirtualScreenTop;
            double right = left + SystemParameters.VirtualScreenWidth;
            double bottom = top + SystemParameters.VirtualScreenHeight;

            if (x + w > right) x = screenDip.X - CursorGap - w;
            if (y + h > bottom) y = screenDip.Y - CursorGap - h;

            Left = Math.Max(left, Math.Min(x, Math.Max(left, right - w)));
            Top = Math.Max(top, Math.Min(y, Math.Max(top, bottom - h)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MaggotTask] 名牌定位失败: {ex.Message}");
        }
    }

    private static Brush BrushFor(Stage stage) =>
        Palette.Frozen(new SolidColorBrush(Palette.StageColor(stage)));
}
