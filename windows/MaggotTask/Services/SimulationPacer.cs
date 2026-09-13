using System.Diagnostics;
using MaggotTask.Core;

namespace MaggotTask.Services;

/// <summary>
/// 固定步长节拍器。
///
/// 规格把 <c>TICK_HZ = 60</c>（前台）与 <c>IDLE_TICK_HZ = 10</c>（悬浮层关闭）
/// 定为**模拟**频率，而渲染频率由显示器决定 —— 二者必须解耦。
/// 如果直接把每帧的真实 dt 喂给引擎，那么：
///   · 144Hz 显示器上腐烂会快 2.4 倍
///   · 掉到 20fps 时又只剩三分之一
/// 都会让「同样放着不管 11 分钟」得到完全不同的结果。
///
/// 这里用经典的固定步长累加器：把真实经过时间切成 1/60 秒的整数份，
/// 一份一份推进引擎；最多补 <see cref="MaxCatchUpSteps"/> 步，
/// 避免卡顿后出现「补帧雪崩」。
/// </summary>
public sealed class SimulationPacer
{
    /// <summary>单帧最多补多少步，防止卡顿后一次性狂追。</summary>
    public const int MaxCatchUpSteps = 8;

    private readonly double _stepSeconds;
    private double _accumulator;
    private long _lastTimestamp;
    private bool _primed;

    public SimulationPacer(int hz = LifecycleEngine.TickHz)
    {
        if (hz <= 0) hz = LifecycleEngine.TickHz;
        _stepSeconds = 1.0 / hz;
    }

    /// <summary>固定步长（秒）。</summary>
    public double StepSeconds => _stepSeconds;

    /// <summary>累计被丢弃的时间（秒）—— 卡顿太狠时追不上的那部分。</summary>
    public double DroppedSeconds { get; private set; }

    /// <summary>累计执行过的固定步数。</summary>
    public long StepCount { get; private set; }

    /// <summary>重置计时基准（窗口隐藏/显示切换后调用，避免补一整段空白时间）。</summary>
    public void Reset()
    {
        _primed = false;
        _accumulator = 0;
    }

    /// <summary>
    /// 推进一次。返回本次应当执行的固定步数（0..<see cref="MaxCatchUpSteps"/>）。
    /// </summary>
    public int Advance()
    {
        long now = Stopwatch.GetTimestamp();

        if (!_primed)
        {
            _primed = true;
            _lastTimestamp = now;
            return 0;
        }

        double elapsed = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;

        if (elapsed <= 0) return 0;

        // 单帧最多承认 MAX_DT(0.5s)，与引擎的 dt clamp 保持一致。
        if (elapsed > LifecycleEngine.MaxDt) elapsed = LifecycleEngine.MaxDt;

        _accumulator += elapsed;

        int steps = 0;
        while (_accumulator >= _stepSeconds && steps < MaxCatchUpSteps)
        {
            _accumulator -= _stepSeconds;
            steps++;
        }

        if (steps >= MaxCatchUpSteps && _accumulator >= _stepSeconds)
        {
            // 追不上了：丢弃余量，宁可慢一点也不要雪崩。
            DroppedSeconds += _accumulator;
            _accumulator = 0;
        }

        StepCount += steps;
        return steps;
    }
}
