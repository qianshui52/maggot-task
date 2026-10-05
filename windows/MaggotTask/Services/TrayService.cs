using MaggotTask.Render;

namespace MaggotTask.Services;

/// <summary>
/// 系统托盘图标（System.Windows.Forms.NotifyIcon）。
/// 图标优先取嵌入的应用图标（Assets\maggot.ico 的 32px 帧，与 exe 图标同一份），
/// 读不到时退回 <see cref="TrayIconFactory"/> 的程序化绘制 —— 两处都不引用外部文件。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _ownedIcon;
    private bool _disposed;

    public TrayService()
    {
        _ownedIcon = AppIconFactory.LoadTrayIcon(32) ?? TrayIconFactory.Create(32);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _ownedIcon,
            Text = "蛆虫任务 · 正在腐化",
            Visible = true,
        };

        var menu = new System.Windows.Forms.ContextMenuStrip { ShowImageMargin = false };
        _panelItem = NewItem("隐藏控制面板", () => TogglePanelRequested?.Invoke());
        menu.Items.Add(_panelItem);
        menu.Items.Add(NewItem("显示/隐藏虫群", () => ToggleSwarmRequested?.Invoke()));
        _startPauseItem = NewItem("启动/暂停", () => ToggleRunningRequested?.Invoke());
        menu.Items.Add(_startPauseItem);
        menu.Items.Add(NewItem("打开任务列表", () => OpenTaskListRequested?.Invoke()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(NewItem("退出", () => ExitRequested?.Invoke()));

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => DoubleClickRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke();
    }

    private readonly System.Windows.Forms.ToolStripMenuItem _startPauseItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _panelItem;

    public event Action? TogglePanelRequested;
    public event Action? ToggleSwarmRequested;
    public event Action? ToggleRunningRequested;
    public event Action? OpenTaskListRequested;
    public event Action? ExitRequested;
    public event Action? DoubleClickRequested;
    public event Action? BalloonClicked;

    /// <summary>
    /// 同步「显示/隐藏控制面板」的文案。
    /// 面板上的「收起」按钮也会隐藏面板 —— 那个动作走的是同一条
    /// <see cref="TogglePanelRequested"/> 路径，所以这里必须能被反向调用，
    /// 否则菜单文案会和实际可见性脱钩。
    /// </summary>
    public void SetPanelVisible(bool visible)
    {
        if (_disposed) return;
        _panelItem.Text = visible ? "隐藏控制面板" : "显示控制面板";
    }

    /// <summary>当前菜单文案（诊断用）。</summary>
    public string PanelMenuText => _panelItem.Text;

    private static System.Windows.Forms.ToolStripMenuItem NewItem(string text, Action action)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>同步托盘提示与「启动/暂停」菜单文案。</summary>
    public void UpdateStatus(bool running, int taskCount, int flyCount)
    {
        if (_disposed) return;
        _startPauseItem.Text = running ? "暂停" : "启动";
        string text = running
            ? $"蛆虫任务 · 运行中 · {taskCount} 只 · 蝇 {flyCount}"
            : $"蛆虫任务 · 已暂停 · {taskCount} 只";
        // NotifyIcon.Text 上限 63 字符，中文按字符算。
        _icon.Text = text.Length > 62 ? text[..62] : text;
    }

    /// <summary>气泡通知。</summary>
    public void Notify(string title, string message, bool warning = false)
    {
        if (_disposed) return;
        try
        {
            _icon.ShowBalloonTip(
                timeout: 6000,
                tipTitle: title,
                tipText: message.Length > 250 ? message[..250] : message,
                tipIcon: warning
                    ? System.Windows.Forms.ToolTipIcon.Warning
                    : System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MaggotTask] 气泡通知失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _ownedIcon.Dispose();
    }
}
