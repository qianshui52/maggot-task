namespace MaggotTask.Core;

/// <summary>击杀工具的命中形状。</summary>
public enum ToolShape
{
    /// <summary>单点：只打光标下最近的一只（苍蝇拍）。</summary>
    Single = 0,

    /// <summary>圆形范围（杀虫喷雾）。</summary>
    Circle = 1,

    /// <summary>十字斩：横竖两条带（唐太刀）。</summary>
    Cross = 2,

    /// <summary>全屏横向光带（霓虹激光枪）。</summary>
    Beam = 3,
}

/// <summary>
/// 一件杀虫工具。纯数据，无 WPF 依赖 —— 命中判定与商店都读它。
///
/// 设计约束：**工具不改变击杀奖励**。奖励只由阶段决定（卵 100 / 蛆 50 / 蛹 25 / 苍蝇 10），
/// 否则"幼虫比成虫值钱"这条核心激励会被工具倍率搅乱，数值也就不可对账了。
/// 进阶工具改变的是**一次能清掉几只**（范围）与**出手间隔**（冷却）。
/// </summary>
/// <param name="Id">稳定 id，落盘用。</param>
/// <param name="Name">显示名。</param>
/// <param name="Subtitle">商店卡片上的一句话定位。</param>
/// <param name="Description">详细说明。</param>
/// <param name="Price">价格（货币）。0 = 初始拥有。</param>
/// <param name="Shape">命中形状。</param>
/// <param name="Radius">
/// Single = 0（按生物尺寸）；Circle = 半径；Cross = 臂长；Beam 忽略。
/// </param>
/// <param name="Width">Cross / Beam 的带宽半值（px）。</param>
/// <param name="CooldownSeconds">两次出手之间的最短间隔（秒）。0 = 无冷却。</param>
/// <param name="AccentHex">光标与特效的强调色。</param>
public sealed record ToolDef(
    string Id,
    string Name,
    string Subtitle,
    string Description,
    int Price,
    ToolShape Shape,
    double Radius,
    double Width,
    double CooldownSeconds,
    string AccentHex);

/// <summary>
/// 工具目录。价格是用户指定的 1000 / 10000 / 50000；
/// 苍蝇拍为初始装备，价格 0。
/// </summary>
public static class ToolCatalog
{
    public const string SwatterId = "swatter";
    public const string SprayId = "spray";
    public const string KatanaId = "katana";
    public const string LaserId = "laser";

    public static readonly ToolDef Swatter = new(
        SwatterId,
        "苍蝇拍",
        "初始装备 · 一只一只来",
        "最朴素也最可靠。点中哪只打哪只，没有冷却，随时可以出手。",
        0,
        ToolShape.Single,
        0,
        0,
        0,
        "#E8EDE0");

    public static readonly ToolDef Spray = new(
        SprayId,
        "杀虫喷雾",
        "范围清扫 · 一片一片来",
        "喷一圈 130px 的雾，圈里的全部清掉。冷却 0.9 秒。适合虫群扎堆时使用。",
        1_000,
        ToolShape.Circle,
        130,
        0,
        0.90,
        "#8ACE00");

    public static readonly ToolDef Katana = new(
        KatanaId,
        "唐太刀",
        "十字斩 · 横竖一起断",
        "一刀劈出十字，横竖各 240px、带内全部清空。冷却 0.55 秒。",
        10_000,
        ToolShape.Cross,
        240,
        46,
        0.55,
        "#E0A32E");

    public static readonly ToolDef Laser = new(
        LaserId,
        "霓虹激光枪",
        "全屏光带 · 一层一层削",
        "沿光标高度横扫整块屏幕，上下 42px 内的虫子同时消失。冷却 1.1 秒。",
        50_000,
        ToolShape.Beam,
        0,
        42,
        1.10,
        "#4FA98E");

    /// <summary>商店里的顺序（也是价格升序）。</summary>
    public static readonly IReadOnlyList<ToolDef> All = new[] { Swatter, Spray, Katana, Laser };

    /// <summary>可购买的工具（不含初始苍蝇拍）。</summary>
    public static readonly IReadOnlyList<ToolDef> Purchasable = new[] { Spray, Katana, Laser };

    /// <summary>按 id 取；未知 id 退回苍蝇拍（存档被手改也不至于崩）。</summary>
    public static ToolDef Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return Swatter;
        foreach (ToolDef t in All)
        {
            if (string.Equals(t.Id, id, StringComparison.Ordinal)) return t;
        }
        return Swatter;
    }

    /// <summary>阶段基础奖励。用户指定：苍蝇 10 / 蛆 50 / 卵 100。</summary>
    /// <remarks>
    /// 蛹在需求里没给数字。按"越早清越值钱"的单调递减关系插值取 25，
    /// 并在勋章/商店文案里按 25 展示，保证三处口径一致。
    /// </remarks>
    public static int RewardFor(Stage stage) => stage switch
    {
        Stage.Egg => 100,
        Stage.Larva => 50,
        Stage.Pupa => 25,
        Stage.Fly => 10,
        _ => 0,
    };
}
