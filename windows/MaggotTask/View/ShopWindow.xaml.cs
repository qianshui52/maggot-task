using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MaggotTask.Core;
using MaggotTask.Render;

namespace MaggotTask.View;

/// <summary>
/// 军械库：用击杀赚来的货币购买进阶杀虫工具。
///
/// 价格与效果全部来自 <see cref="ToolCatalog"/>，界面不写第二份数值 ——
/// 这样"商店里写的"和"实际打出来的"不可能对不上。
/// </summary>
public partial class ShopWindow : Window
{
    private readonly IAppHost _host;
    private readonly ShopViewModel _vm;
    private readonly DispatcherTimer _refreshTimer;

    public ShopWindow(IAppHost host)
    {
        _host = host;
        InitializeComponent();

        _vm = new ShopViewModel(host);
        DataContext = _vm;

        SourceInitialized += (_, _) =>
        {
            try
            {
                WindowStyles.ApplyToolWindowStyle(this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MaggotTask] 军械库样式失败: {ex.Message}");
            }
        };

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _refreshTimer.Tick += (_, _) => _vm.Refresh();
        _refreshTimer.Start();

        Closed += (_, _) => _refreshTimer.Stop();
        _vm.Refresh();

        // Activate() 会把焦点丢给第一个按钮，ScrollViewer 顺势把它 BringIntoView ——
        // 于是窗口一打开就"自动滚到中间"，标题下面的说明被切掉。强制回到顶部。
        Loaded += (_, _) => BodyScroll.ScrollToTop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) BodyScroll.ScrollToTop();
        };
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Hide();
        e.Handled = true;
    }
}

/// <summary>军械库视图模型。</summary>
public sealed class ShopViewModel : ViewModelBase
{
    private readonly IAppHost _host;
    private string _coinText = "0";

    public ShopViewModel(IAppHost host)
    {
        _host = host;

        foreach (ToolDef tool in ToolCatalog.All)
        {
            Cards.Add(new ShopCardViewModel(host, tool));
        }
    }

    public ObservableCollection<ShopCardViewModel> Cards { get; } = new();

    /// <summary>右上角余额。</summary>
    public string CoinText { get => _coinText; private set => SetField(ref _coinText, value); }

    public string FooterText =>
        $"当前装备：{_host.Progress.EquippedToolDef.Name} · 已解锁 {_host.Progress.UnlockedMedalCount}/{GameProgress.MedalCatalog.Count} 枚勋章";

    /// <summary>从进度对象重新拉一遍（货币变化时刷新按钮可用性）。</summary>
    public void Refresh()
    {
        CoinText = $"{_host.Progress.Coins:N0} 货币";
        foreach (ShopCardViewModel c in Cards) c.Refresh();
        Raise(nameof(FooterText));
    }
}

/// <summary>一张工具卡片。</summary>
public sealed class ShopCardViewModel : ViewModelBase
{
    private readonly IAppHost _host;
    private readonly ToolDef _tool;

    private string _priceText = string.Empty;
    private string _actionText = string.Empty;
    private string _badge = string.Empty;
    private bool _canAct;

    public ShopCardViewModel(IAppHost host, ToolDef tool)
    {
        _host = host;
        _tool = tool;
        Action = new RelayCommand(Act);
        AccentBrush = Palette.Frozen(new SolidColorBrush(
            (Color)System.Windows.Media.ColorConverter.ConvertFromString(tool.AccentHex)!));
        Refresh();
    }

    public string Name => _tool.Name;
    public string Subtitle => _tool.Subtitle;
    public string Description => _tool.Description;
    public Brush AccentBrush { get; }
    public ICommand Action { get; }

    public string Badge { get => _badge; private set => SetField(ref _badge, value); }
    public string PriceText { get => _priceText; private set => SetField(ref _priceText, value); }
    public string ActionText { get => _actionText; private set => SetField(ref _actionText, value); }
    public bool CanAct { get => _canAct; private set => SetField(ref _canAct, value); }

    public void Refresh()
    {
        GameProgress p = _host.Progress;
        bool owned = p.Owns(_tool.Id);
        bool equipped = string.Equals(p.EquippedTool, _tool.Id, StringComparison.Ordinal);
        bool affordable = p.Coins >= _tool.Price;

        Badge = _tool.Price == 0 ? "初始" : (_tool.Price >= 50_000 ? "传说" : "进阶");

        if (equipped)
        {
            PriceText = "已装备";
            ActionText = "使用中";
            CanAct = false;
        }
        else if (owned)
        {
            PriceText = "已拥有";
            ActionText = "装备";
            CanAct = true;
        }
        else
        {
            PriceText = $"价格 {_tool.Price:N0} 货币";
            ActionText = affordable ? "购买" : "货币不足";
            CanAct = affordable;
        }
    }

    private void Act()
    {
        if (_host.Progress.Owns(_tool.Id))
        {
            _host.EquipTool(_tool.Id);
        }
        else
        {
            _host.BuyTool(_tool.Id);
        }
    }
}
