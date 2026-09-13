using System.Windows.Media;
using MaggotTask.Core;
using MaggotTask.Render;

namespace MaggotTask.View;

/// <summary>控制面板视图模型（设计系统 §7）。</summary>
public sealed class ControlPanelViewModel : ViewModelBase
{
    private readonly IAppHost _host;

    private int _egg, _larva, _pupa, _fly, _dead;
    private int _total;
    private double _averageRot;
    private double _eggRatio, _larvaRatio, _pupaRatio, _flyRatio;
    private bool _running = true;
    private double _rotSpeed = 1.0;
    private bool _clickThrough = true;
    private bool _sound = true;
    private bool _notify = true;
    private bool _daily = true;
    private bool _weekly = true;
    private string _lastSeenText = "已同步";
    private long _squashed;

    public ControlPanelViewModel(IAppHost host)
    {
        _host = host;

        RunCommand = new RelayCommand(() => _host.ToggleRunning());
        NewTaskCommand = new RelayCommand(() => _host.OpenTaskList());
        ResetCommand = new RelayCommand(() => _host.ResetAllTasks());
        ClearCommand = new RelayCommand(() => _host.ClearAllTasks());

        _running = _host.Config.Running;
        _rotSpeed = _host.Config.RotSpeed;
        _clickThrough = _host.Config.ClickThrough;
        _sound = _host.Config.SoundEnabled;
        _notify = _host.Config.NotifyOnFly;
        _daily = _host.Config.DailyEnabled;
        _weekly = _host.Config.WeeklyEnabled;
    }

    public RelayCommand RunCommand { get; }
    public RelayCommand NewTaskCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ClearCommand { get; }

    public int EggCount { get => _egg; private set => SetField(ref _egg, value); }
    public int LarvaCount { get => _larva; private set => SetField(ref _larva, value); }
    public int PupaCount { get => _pupa; private set => SetField(ref _pupa, value); }
    public int FlyCount { get => _fly; private set => SetField(ref _fly, value); }

    public double EggRatio { get => _eggRatio; private set => SetField(ref _eggRatio, value); }
    public double LarvaRatio { get => _larvaRatio; private set => SetField(ref _larvaRatio, value); }
    public double PupaRatio { get => _pupaRatio; private set => SetField(ref _pupaRatio, value); }
    public double FlyRatio { get => _flyRatio; private set => SetField(ref _flyRatio, value); }

    public string AverageRotText => $"{_averageRot * 100.0:0}%";

    public string TotalText => $"共 {_total} 个任务";
    public string SquashedText => $"{_squashed} 个";
    public string LastSeenText { get => _lastSeenText; set => SetField(ref _lastSeenText, value); }

    /// <summary>
    /// 腐烂压力条下方那一行。用**一个绑定的字符串**而不是多个 <c>Run</c>：
    /// TextBlock 带内联时不支持 TextTrimming，面板宽度固定 348，
    /// 长文案会直接被裁掉而不是显示省略号。
    /// </summary>
    public string StatusRowText =>
        $"标本缸 01 · 共 {_total} 个任务 · 拍死 {_squashed} 个 · {_lastSeenText}";

    public bool Running
    {
        get => _running;
        private set
        {
            if (SetField(ref _running, value))
            {
                Raise(nameof(RunButtonText));
                Raise(nameof(StatusText));
                Raise(nameof(StatusDotBrush));
            }
        }
    }

    public string RunButtonText => _running ? "暂停" : "启动";

    public string StatusText => _running ? "运行中" : "已暂停";

    /// <summary>运行状态点：绿（--primary）= 运行 / 灰（--fg-subtle）= 暂停。</summary>
    public Brush StatusDotBrush => _running
        ? Palette.Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0xCE, 0x00)))
        : Palette.Frozen(new SolidColorBrush(Color.FromRgb(0x7A, 0x8A, 0x6C)));

    public double RotSpeed
    {
        get => _rotSpeed;
        set
        {
            double clamped = Math.Clamp(value, 0.25, 8.0);
            if (SetField(ref _rotSpeed, clamped))
            {
                Raise(nameof(RotSpeedText));
                _host.Config.RotSpeed = clamped;
                _host.MarkDirty();
            }
        }
    }

    public string RotSpeedText => $"×{_rotSpeed:0.0#}";

    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (SetField(ref _clickThrough, value))
            {
                _host.Config.ClickThrough = value;
                _host.MarkDirty();
                _host.ApplyClickThrough();
                Raise(nameof(FooterText));
            }
        }
    }

    /// <summary>
    /// 音效开关。规格 §13：**打开时立刻试听「拍死」** ——
    /// 否则用户只会以为坏了。
    /// </summary>
    public bool SoundEnabled
    {
        get => _sound;
        set
        {
            if (SetField(ref _sound, value))
            {
                _host.Config.SoundEnabled = value;
                _host.Sound.Enabled = value;
                if (value) _host.Sound.Preview();
                _host.MarkDirty();
                Raise(nameof(FooterText));
            }
        }
    }

    /// <summary>每日任务总开关（规格 §12.4）。</summary>
    public bool DailyEnabled
    {
        get => _daily;
        set
        {
            if (SetField(ref _daily, value))
            {
                _host.Config.DailyEnabled = value;
                _host.MarkDirty();
                Raise(nameof(DailySummaryText));
                Raise(nameof(FooterText));
            }
        }
    }

    /// <summary>每日模板概览：今天有 N 个实例还没做完。</summary>
    public string DailySummaryText
    {
        get
        {
            if (!_daily) return "已关闭";
            IReadOnlyList<DailyTemplate> templates = _host.DailyTemplates();
            if (templates.Count == 0) return "无模板";
            int pending = 0;
            foreach (DailyTemplate t in templates)
            {
                if (!t.Enabled) continue;
                string status = _host.DailyInstanceStatus(t);
                if (status != "已完成") pending++;
            }
            return $"{templates.Count} 个模板 · 今日待办 {pending}";
        }
    }

    /// <summary>每周任务总开关（规格 §15.5 → §12.4）。</summary>
    public bool WeeklyEnabled
    {
        get => _weekly;
        set
        {
            if (SetField(ref _weekly, value))
            {
                _host.Config.WeeklyEnabled = value;
                _host.MarkDirty();
                Raise(nameof(WeeklySummaryText));
                Raise(nameof(FooterText));
            }
        }
    }

    /// <summary>每周模板概览：本周有 N 个实例还没做完。</summary>
    public string WeeklySummaryText
    {
        get
        {
            if (!_weekly) return "已关闭";
            IReadOnlyList<WeeklyTemplate> templates = _host.WeeklyTemplates();
            if (templates.Count == 0) return "无模板";
            int pending = 0;
            foreach (WeeklyTemplate t in templates)
            {
                if (!t.Enabled) continue;
                string status = _host.WeeklyInstanceStatus(t);
                if (status != "已完成") pending++;
            }
            return $"{templates.Count} 个模板 · 本周待办 {pending}";
        }
    }

    public bool NotifyOnFly
    {
        get => _notify;
        set
        {
            if (SetField(ref _notify, value))
            {
                _host.Config.NotifyOnFly = value;
                _host.MarkDirty();
            }
        }
    }

    public string FooterText =>
        $"穿透 {( _clickThrough ? "开" : "关")} · 音效 {(_sound ? "开" : "关")} · 通知 {(_notify ? "开" : "关")}"
        + $" · 每日 {(_daily ? "开" : "关")}"
        + $" · 每周 {(_weekly ? "开" : "关")}"
        + (_host.ReduceMotion ? " · 已减少动效" : string.Empty);

    /// <summary>从引擎与配置重新拉取全部展示值。</summary>
    public void Refresh()
    {
        var counts = _host.StageCounts();
        EggCount = counts.Egg;
        LarvaCount = counts.Larva;
        PupaCount = counts.Pupa;
        FlyCount = counts.Fly;
        _dead = counts.Dead;

        int live = counts.Egg + counts.Larva + counts.Pupa + counts.Fly;
        _total = _host.TaskCount;
        Raise(nameof(TotalText));
        Raise(nameof(StatusRowText));

        double sum = live > 0 ? (double)live : 1.0;
        EggRatio = counts.Egg / sum;
        LarvaRatio = counts.Larva / sum;
        PupaRatio = counts.Pupa / sum;
        FlyRatio = counts.Fly / sum;

        double avg = _host.AverageRot;
        if (Math.Abs(avg - _averageRot) > 0.0005)
        {
            _averageRot = avg;
            Raise(nameof(AverageRotText));
        }

        _squashed = (int)_host.SquashedCount;
        Raise(nameof(SquashedText));
        Raise(nameof(StatusRowText));

        Running = _host.Config.Running;
        _rotSpeed = _host.Config.RotSpeed;
        Raise(nameof(RotSpeed));
        Raise(nameof(RotSpeedText));

        _clickThrough = _host.Config.ClickThrough;
        Raise(nameof(ClickThrough));
        _sound = _host.Config.SoundEnabled;
        Raise(nameof(SoundEnabled));
        _notify = _host.Config.NotifyOnFly;
        Raise(nameof(NotifyOnFly));
        _daily = _host.Config.DailyEnabled;
        Raise(nameof(DailyEnabled));
        Raise(nameof(DailySummaryText));
        _weekly = _host.Config.WeeklyEnabled;
        Raise(nameof(WeeklyEnabled));
        Raise(nameof(WeeklySummaryText));
        Raise(nameof(FooterText));
        Raise(nameof(StatusDotBrush));
    }

    /// <summary>离线补算 / 保存状态的文案。</summary>
    public void SetStatusLine(string text)
    {
        LastSeenText = text;
        Raise(nameof(StatusRowText));
    }
}
