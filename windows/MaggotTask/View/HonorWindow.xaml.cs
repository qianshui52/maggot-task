using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MaggotTask.Core;
using MaggotTask.Render;

namespace MaggotTask.View;

/// <summary>
/// 荣誉殿堂：连胜热度 + 连胜日历 + 十枚勋章。
///
/// 三个区块共用同一份 <see cref="GameProgress"/>：
///   · 连胜卡片读 CurrentStreak / BestStreak
///   · 日历读 Days[]
///   · 勋章读击杀计数与 BestStreak
/// 没有任何一个区块自己维护计数，所以它们不可能互相矛盾。
/// </summary>
public partial class HonorWindow : Window
{
    /// <summary>日历展示的周数（5 周 = 35 天）。</summary>
    public const int CalendarWeeks = 5;

    private static readonly Brush[] DayBrushes =
    {
        Brushes.Transparent,                                             // 0（不用）
        Frozen("#FF2F4A22"),
        Frozen("#FF446F29"),
        Frozen("#FF5E9633"),
        Frozen("#FF74B43C"),
        Frozen("#FF8ACE00"),
    };

    private static readonly Brush InactiveBrush = Frozen("#FF202A1B");
    private static readonly Brush FutureBrush = Frozen("#FF11170E");
    private static readonly Brush TodayBorder = Frozen("#FF8ACE00");
    private static readonly Brush CellBorder = Frozen("#FF2A3624");
    private static readonly Brush DarkText = Frozen("#FF0A0C08");
    private static readonly Brush LightText = Frozen("#FFE8EDE0");
    private static readonly Brush MutedText = Frozen("#FF7A8A6C");

    private readonly IAppHost _host;
    private readonly HonorViewModel _vm;
    private readonly DispatcherTimer _refreshTimer;

    public HonorWindow(IAppHost host)
    {
        _host = host;
        InitializeComponent();

        _vm = new HonorViewModel(host);
        DataContext = _vm;

        SourceInitialized += (_, _) =>
        {
            try
            {
                WindowStyles.ApplyToolWindowStyle(this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MaggotTask] 荣誉殿堂样式失败: {ex.Message}");
            }
        };

        BuildWeekdayHeader();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _refreshTimer.Tick += (_, _) => RefreshAll();
        _refreshTimer.Start();
        Closed += (_, _) => _refreshTimer.Stop();

        RefreshAll();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Hide();
        e.Handled = true;
    }

    /// <summary>重新计算卡片、日历与勋章。</summary>
    public void RefreshAll()
    {
        _vm.Refresh();
        RebuildCalendar();
    }

    private void BuildWeekdayHeader()
    {
        // 周一为一周之始（与 ISO 周一致，也和连胜判定同源）。
        //
        // 用 Grid + 7 个 * 列而不是 UniformGrid：UniformGrid 在 StackPanel 里会
        // 按"内容宽度"测量，导致表头整体缩在中间、和下面的日历列对不齐。
        string[] names = { "一", "二", "三", "四", "五", "六", "日" };
        WeekdayHeader.Children.Clear();
        WeekdayHeader.ColumnDefinitions.Clear();

        for (int i = 0; i < 7; i++)
        {
            WeekdayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = names[i],
                FontFamily = ThemeFonts.Mono,
                FontSize = 10,
                Foreground = MutedText,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2),
            };
            Grid.SetColumn(label, i);
            WeekdayHeader.Children.Add(label);
        }
    }

    private void RebuildCalendar()
    {
        GameProgress p = _host.Progress;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        int dow = ((int)today.DayOfWeek + 6) % 7;          // 周一 = 0
        DateOnly start = today.AddDays(-dow - (CalendarWeeks - 1) * 7);

        CalendarGrid.Children.Clear();

        for (int i = 0; i < CalendarWeeks * 7; i++)
        {
            DateOnly date = start.AddDays(i);
            CalendarGrid.Children.Add(BuildDayCell(p, date, today));
        }
    }

    private FrameworkElement BuildDayCell(GameProgress p, DateOnly date, DateOnly today)
    {
        string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        bool future = date > today;
        DayLog log = p.DayOrEmpty(key);
        int kills = log.Kills;

        int level = future ? 0 : kills switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 or 4 => 3,
            5 or 6 or 7 => 4,
            _ => 5,
        };

        Brush background = future
            ? FutureBrush
            : (level == 0 ? (log.Active ? Frozen("#FF35502A") : InactiveBrush) : DayBrushes[level]);

        Brush foreground = future
            ? Frozen("#FF33402C")
            : (level >= 3 ? DarkText : LightText);

        var grid = new Grid();
        grid.Children.Add(new TextBlock
        {
            Text = date.Day.ToString(CultureInfo.InvariantCulture),
            FontFamily = ThemeFonts.Mono,
            FontSize = 10,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 2, 0, 0),
        });

        if (!future && kills > 0)
        {
            grid.Children.Add(new TextBlock
            {
                Text = kills.ToString(CultureInfo.InvariantCulture),
                FontFamily = ThemeFonts.Mono,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 4, 2),
            });
        }

        var cell = new Border
        {
            Height = 42,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Background = background,
            BorderBrush = date == today ? TodayBorder : CellBorder,
            BorderThickness = new Thickness(date == today ? 1.5 : 1),
            Child = grid,
        };

        string tip = future
            ? $"{date:MM-dd} · 还没到"
            : (log.Active
                ? $"{date:MM-dd} · 击杀 {log.Kills} · 完成 {log.Completed} · +{log.Coins} 货币"
                : $"{date:MM-dd} · 没有打卡");
        cell.ToolTip = tip;

        return cell;
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }
}

/// <summary>荣誉殿堂视图模型。</summary>
public sealed class HonorViewModel : ViewModelBase
{
    private readonly IAppHost _host;

    public HonorViewModel(IAppHost host)
    {
        _host = host;
        foreach (MedalDef m in GameProgress.MedalCatalog)
        {
            Medals.Add(new MedalCardViewModel(host, m));
        }
    }

    public ObservableCollection<MedalCardViewModel> Medals { get; } = new();

    public string StreakText => _host.Progress.CurrentStreak.ToString(CultureInfo.InvariantCulture);

    public string HeatLabel => _host.Progress.HeatLabel;

    public string HeatFlames
    {
        get
        {
            int lvl = _host.Progress.HeatLevel;
            return lvl <= 0 ? "· · · · ·" : string.Concat(Enumerable.Repeat("◆ ", lvl)).TrimEnd() + new string('·', Math.Max(0, 5 - lvl));
        }
    }

    public string StreakHint
    {
        get
        {
            GameProgress p = _host.Progress;
            string today = DailyKeys.Today();
            if (p.IsActiveOn(today)) return "今天已经打卡。保持住，别让它断在明天。";
            if (p.CurrentStreak > 0) return "今天还没打卡 —— 清掉一只虫子就算。";
            return "今天清掉一只虫子，连胜就从 1 开始。";
        }
    }

    public string BestText => $"最长纪录 {_host.Progress.BestStreak} 天";

    public string TodayText
    {
        get
        {
            DayLog log = _host.Progress.DayOrEmpty(DailyKeys.Today());
            return $"今日击杀 {log.Kills} · +{log.Coins} 货币";
        }
    }

    public string MedalCountText => $"勋章 {_host.Progress.UnlockedMedalCount}/{GameProgress.MedalCatalog.Count}";

    public string FooterText =>
        $"累计击杀 {_host.Progress.TotalKills} · 卵 {_host.Progress.KillsEgg} / 蛆 {_host.Progress.KillsLarva} / 蛹 {_host.Progress.KillsPupa} / 苍蝇 {_host.Progress.KillsFly}"
        + $" · 累计获得 {_host.Progress.TotalCoinsEarned:N0} 货币";

    public void Refresh()
    {
        Raise(nameof(StreakText));
        Raise(nameof(HeatLabel));
        Raise(nameof(HeatFlames));
        Raise(nameof(StreakHint));
        Raise(nameof(BestText));
        Raise(nameof(TodayText));
        Raise(nameof(MedalCountText));
        Raise(nameof(FooterText));

        foreach (MedalCardViewModel m in Medals) m.Refresh();
    }
}

/// <summary>一枚勋章在墙上的呈现。</summary>
public sealed class MedalCardViewModel : ViewModelBase
{
    private readonly IAppHost _host;
    private readonly MedalDef _medal;

    public MedalCardViewModel(IAppHost host, MedalDef medal)
    {
        _host = host;
        _medal = medal;
        Refresh();
    }

    public string Glyph => _medal.Glyph;
    public string Name => _medal.Name;
    public string Description => _medal.Description;

    public string ProgressText { get; private set; } = string.Empty;
    public double BarWidth { get; private set; }
    public Brush BarBrush { get; private set; } = Brushes.Transparent;
    public Brush BadgeBackground { get; private set; } = Brushes.Transparent;
    public Brush GlyphBrush { get; private set; } = Brushes.Transparent;
    public double CardOpacity { get; private set; } = 1.0;

    public void Refresh()
    {
        GameProgress p = _host.Progress;
        bool unlocked = p.MedalReached(_medal);
        int value = Math.Min(p.MedalValue(_medal), _medal.Target);
        double ratio = p.MedalProgress(_medal);

        ProgressText = unlocked
            ? $"已达成 · {value}/{_medal.Target} {_medal.Unit}"
            : $"{value}/{_medal.Target} {_medal.Unit}";

        BarWidth = Math.Max(0, 150.0 * ratio);
        BarBrush = unlocked
            ? Palette.Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0xCE, 0x00)))
            : Palette.Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xA9, 0x8E)));

        BadgeBackground = unlocked
            ? Palette.Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x2C, 0x18)))
            : Palette.Frozen(new SolidColorBrush(Color.FromRgb(0x15, 0x1D, 0x12)));

        GlyphBrush = unlocked
            ? Palette.Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x2E)))
            : Palette.Frozen(new SolidColorBrush(Color.FromRgb(0x55, 0x60, 0x4A)));

        CardOpacity = unlocked ? 1.0 : 0.62;

        Raise(nameof(ProgressText));
        Raise(nameof(BarWidth));
        Raise(nameof(BarBrush));
        Raise(nameof(BadgeBackground));
        Raise(nameof(GlyphBrush));
        Raise(nameof(CardOpacity));
    }
}
