using System.Windows;

namespace MaggotTask.View;

/// <summary>
/// 悬停命中的极简目标：一只生物在**覆盖层本地坐标系**里的位置与尺寸。
/// 刻意不依赖 <c>CreatureVisual</c>，这样驻留逻辑可以脱离 WPF 单测。
/// </summary>
public readonly record struct HoverTarget(string TaskId, Point Position, double Size);

/// <summary>
/// 规格 §14 的悬停驻留逻辑 —— **纯逻辑，无 WPF / 无 Win32 依赖**，可以单测。
///
/// 关键约束（§14.1）：虫群层是 WS_EX_TRANSPARENT，收不到任何鼠标消息，
/// 所以这里**不装全局钩子、不订阅鼠标事件**，只消费调用方轮询来的光标位置。
/// 调用方（<see cref="HoverBubbleWindow"/> 的宿主）每 ~100ms 调一次 <see cref="Poll"/>。
///
/// 行为（§14.3）：
///   · 驻留计时**按生物个体**归零：鼠标从 A 划到 B 要重新数 3 秒
///   · 弹过一次之后，只要还停在同一只身上就保持；一旦移开立刻收起
///   · 命中判定用当帧位置，允许生物漂移
/// </summary>
public sealed class HoverTracker
{
    /// <summary>触发所需的驻留时长（秒）。规格 §14：3.0s。</summary>
    public const double DwellSeconds = 3.0;

    /// <summary>轮询间隔（秒）。规格 §14.1：约 10–15Hz。</summary>
    public const double PollIntervalSeconds = 0.1;

    /// <summary>
    /// 命中半径系数：r = size × 0.7（规格 §14.1「至少 ~0.7 × 它的 size」）。
    /// </summary>
    public const double HitRadiusScale = 0.7;

    private string? _hoveredId;
    private double _dwell;
    private bool _shown;
    private readonly double _dwellSeconds;

    /// <summary>
    /// 默认 3 秒（规格 §14）。悬停名牌用 0.18 秒 —— 同一条驻留逻辑，
    /// 只是触发阈值不同，不需要第二套实现。
    /// </summary>
    public HoverTracker(double dwellSeconds = DwellSeconds)
    {
        _dwellSeconds = Math.Max(0, dwellSeconds);
    }

    /// <summary>本实例的触发阈值（秒）。</summary>
    public double DwellTarget => _dwellSeconds;

    /// <summary>当前驻留计时（秒）。诊断用。</summary>
    public double Dwell => _dwell;

    /// <summary>当前悬停的生物 id（没有则为 null）。</summary>
    public string? HoveredId => _hoveredId;

    /// <summary>气泡当前是否应该显示。</summary>
    public bool IsShown => _shown;

    /// <summary>统计：气泡弹出过几次（诊断用）。</summary>
    public int ShowCount { get; private set; }

    /// <summary>统计：驻留因为换目标而重新计时几次（诊断用）。</summary>
    public int RestartCount { get; private set; }

    /// <summary>立即清空悬停状态（例如虫群层被隐藏、面板被关掉）。</summary>
    public void Reset()
    {
        _hoveredId = null;
        _dwell = 0;
        _shown = false;
    }

    /// <summary>
    /// 一次轮询。
    /// </summary>
    /// <param name="targets">当帧全部生物的位置快照。</param>
    /// <param name="cursor">当帧全局光标位置（已换算到覆盖层本地坐标）。</param>
    /// <param name="dt">距上次轮询的秒数。</param>
    /// <returns>本次轮询后的状态。</returns>
    public HoverPollResult Poll(IReadOnlyList<HoverTarget> targets, Point cursor, double dt)
    {
        if (dt < 0) dt = 0;

        HoverTarget? hit = HitTest(targets, cursor);

        if (hit is null)
        {
            // 移开 → 立刻收起，不残留（验收标准 4）
            bool wasShown = _shown;
            bool wasHovering = _hoveredId is not null;
            _hoveredId = null;
            _dwell = 0;
            _shown = false;
            return new HoverPollResult(null, false, wasShown || wasHovering, cursor);
        }

        string id = hit.Value.TaskId;

        if (!string.Equals(id, _hoveredId, StringComparison.Ordinal))
        {
            // 换了目标 → 驻留归零（验收标准 3）。第一次发现这只目标时算 t=0，
            // 所以「停 3.0 秒」在 10Hz 轮询下正好等于 30 次轮询。
            if (_hoveredId is not null) RestartCount++;
            _hoveredId = id;
            _dwell = 0;
            _shown = false;
        }
        else
        {
            _dwell += dt;
        }

        bool newlyShown = false;
        if (!_shown && _dwell >= _dwellSeconds)
        {
            _shown = true;
            ShowCount++;
            newlyShown = true;
        }

        return new HoverPollResult(id, _shown, false, cursor, newlyShown);
    }

    /// <summary>找出光标下最近的一只；距离超过命中半径返回 null。</summary>
    public static HoverTarget? HitTest(IReadOnlyList<HoverTarget> targets, Point cursor)
    {
        HoverTarget? best = null;
        double bestDist = double.MaxValue;

        for (int i = 0; i < targets.Count; i++)
        {
            HoverTarget t = targets[i];
            double r = t.Size * HitRadiusScale;
            if (r <= 0) continue;

            double dx = t.Position.X - cursor.X;
            double dy = t.Position.Y - cursor.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 <= r * r && d2 < bestDist)
            {
                best = t;
                bestDist = d2;
            }
        }

        return best;
    }
}

/// <summary>一次轮询的结果。</summary>
public readonly record struct HoverPollResult(
    string? HoveredId,
    bool ShouldShow,
    bool ShouldHide,
    Point Cursor,
    bool NewlyShown = false);
