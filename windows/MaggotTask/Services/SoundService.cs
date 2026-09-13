using System.IO;
using System.Media;
using System.Runtime.InteropServices;

namespace MaggotTask.Services;

/// <summary>
/// 程序化合成音效（规格 §13）。
///
/// **目标是「恶心」，不是「提示」。** 系统自带提示音（<c>SystemSounds.*</c>）一律不合格，
/// 所以这里在内存里合成 PCM、套一个 WAV 头播放 —— 不引入任何音频资源文件，
/// 也不引入任何 NuGet 包，离线构建照样能跑。
///
/// 四个语义（规格 §13 的表）：
///   拍死 Squash  —— 噪声爆裂 + 下行音高扫频 + 快速指数衰减，约 170ms
///   苍蝇嗡鸣 Buzz —— 低频锯齿波 + 慢速颤音，约 400ms
///   孵化 Hatch   —— 极短的低频「啵」+ 轻微噪声，约 90ms
///   繁殖 Bred    —— 两声快速上行的短音，约 260ms
///
/// 硬性规则：
///   · 播放失败**一律静默**（不弹窗、不刷日志、绝不阻塞渲染循环）
///   · 连点保护：同一音效 30ms 内不重复触发
///   · 必须尊重系统静音 —— 交给 winmm 的 PlaySound，它天然走系统音量/静音
///   · 确定性 PRNG：每次启动合成的字节完全一致
/// </summary>
public sealed class SoundService : IDisposable
{
    /// <summary>采样率。22.05kHz 足够表现噪声与低频嗡鸣，缓冲区还小。</summary>
    public const int SampleRate = 22050;

    /// <summary>同一音效的最小重触发间隔（规格 §13：30ms）。</summary>
    public const int RetriggerGuardMs = 30;

    /// <summary>各音效时长（毫秒），与规格 §13 的表一致。</summary>
    public const int SquashMs = 170;
    public const int BuzzMs = 400;
    public const int HatchMs = 90;
    public const int BredMs = 260;

    /// <summary>确定性 PRNG 的固定种子 —— 每次启动的波形必须一模一样。</summary>
    private const uint NoiseSeed = 0x9E3779B9u;

    private readonly object _gate = new();
    private readonly long[] _lastPlayedTicks = new long[4];
    private readonly SoundBuffer[] _buffers;

    private bool _enabled = true;
    private bool _disposed;

    public SoundService()
    {
        _buffers = new[]
        {
            Synthesize(SquashMs, BuildSquash, "拍死"),
            Synthesize(BuzzMs, BuildBuzz, "苍蝇嗡鸣"),
            Synthesize(HatchMs, BuildHatch, "孵化"),
            Synthesize(BredMs, BuildBred, "繁殖"),
        };
        // GCHandle 固定在构造时完成：PlaySound 是异步的，播放期间内存必须钉住。
        foreach (SoundBuffer b in _buffers) b.Pin();
    }

    /// <summary>音效总开关。</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>播放失败的原因（null = 一直正常）。绝不用于打断主流程。</summary>
    public string? LastError { get; private set; }

    /// <summary>累计成功触发次数，自测/诊断用。</summary>
    public int PlayCount { get; private set; }

    // ------------------------------------------------------------------
    // 四个语义
    // ------------------------------------------------------------------

    /// <summary>拍死（爆浆、解压）。</summary>
    public bool PlaySquash() => Play(0);

    /// <summary>变成苍蝇 / 预警（烦人、挥之不去）。</summary>
    public bool PlayAlert() => Play(1);

    /// <summary>卵孵了（黏腻、破壳）。</summary>
    public bool PlayHatch() => Play(2);

    /// <summary>繁殖（更多了、失控）。</summary>
    public bool PlayBred() => Play(3);

    /// <summary>
    /// 试听：开关打开时立刻放一次「拍死」，让用户马上知道是什么声音（规格 §13）。
    /// 与普通播放一样受 30ms 守卫约束，但**不受 <see cref="Enabled"/> 影响**
    /// （这正是开关刚打开的那一刻，状态可能还没同步到位）。
    /// </summary>
    public bool Preview() => Play(0, bypassEnabled: true);

    /// <summary>播放某个音效。</summary>
    /// <returns>true = 真的交给了 winmm；false = 被开关/守卫拦下或播放失败（全部静默）。</returns>
    public bool Play(int index, bool bypassEnabled = false)
    {
        if (_disposed) return false;
        if (!bypassEnabled && !_enabled) return false;
        if (index < 0 || index >= _buffers.Length) return false;

        lock (_gate)
        {
            // 连点保护：同一音效 30ms 内不重复触发（规格 §13）。
            long now = Environment.TickCount64;
            if (now - _lastPlayedTicks[index] < RetriggerGuardMs) return false;
            _lastPlayedTicks[index] = now;

            SoundBuffer buffer = _buffers[index];
            try
            {
                // SND_MEMORY | SND_ASYNC | SND_NODEFAULT：
                //   · 内存缓冲，不碰文件系统
                //   · 立刻返回，绝不阻塞渲染循环
                //   · 失败时不要发出系统默认的「叮」—— 静默就是静默
                bool ok = PlaySound(buffer.Pointer, IntPtr.Zero,
                    SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
                if (ok)
                {
                    PlayCount++;
                    LastError = null;
                }
                else
                {
                    LastError = "PlaySound 返回 false";
                }
                return ok;
            }
            catch (Exception ex)
            {
                // 播放失败一律静默：只留一条内存里的原因，绝不弹窗、绝不抛回渲染循环。
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
    }

    // ------------------------------------------------------------------
    // 诊断 / 自测用的缓冲区统计
    // ------------------------------------------------------------------

    /// <summary>缓冲区统计（时长 / 峰值 / RMS / WAV 头校验）。</summary>
    public readonly record struct SoundStats(
        string Name,
        int SampleCount,
        double DurationMs,
        double Peak,
        double Rms,
        int ByteLength,
        bool ValidWavHeader)
    {
        public override string ToString() =>
            $"{Name}: {DurationMs:0}ms · {SampleCount} 采样 · {ByteLength} 字节 · " +
            $"峰值 {Peak:0.0000} · RMS {Rms:0.0000} · WAV头 {(ValidWavHeader ? "有效" : "无效")}";
    }

    public IReadOnlyList<SoundStats> Stats()
    {
        SoundStats[] result = new SoundStats[_buffers.Length];
        for (int i = 0; i < _buffers.Length; i++) result[i] = _buffers[i].Stats;
        return result;
    }

    /// <summary>取某个音效的原始字节（自测直接解析 WAV 头用）。</summary>
    public byte[] Bytes(int index) => _buffers[index].Bytes;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try
        {
            PlaySound(IntPtr.Zero, IntPtr.Zero, SND_PURGE);
        }
        catch
        {
            // 无所谓
        }

        foreach (SoundBuffer b in _buffers) b.Unpin();
    }

    // ==================================================================
    // 合成
    // ==================================================================

    /// <summary>合成的中间表示：-1..1 的浮点采样。</summary>
    private sealed class Voice
    {
        public readonly double[] Samples;
        public Voice(int count) => Samples = new double[count];
        public int Length => Samples.Length;
    }

    private SoundBuffer Synthesize(int millis, Func<Voice> build, string name)
    {
        Voice voice = build();
        return SoundBuffer.Create(name, EncodeWav(voice.Samples, SampleRate), millis);
    }

    /// <summary>确定性 PRNG（xorshift32）。同一种子必然得到同一串噪声。</summary>
    private struct Noise
    {
        private uint _state;

        public Noise(uint seed) => _state = seed == 0 ? 0x1234567u : seed;

        /// <summary>-1 .. 1</summary>
        public double Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state / 2147483648.0 - 1.0;
        }
    }

    private static double TimeAt(int i) => i / (double)SampleRate;

    /// <summary>① 拍死：噪声爆裂 + 下行音高扫频 + 快速指数衰减（约 170ms）。</summary>
    private static Voice BuildSquash()
    {
        int n = (int)Math.Round(SampleRate * SquashMs / 1000.0);
        Voice v = new(n);
        Noise noise = new(NoiseSeed);

        // 一阶低通状态：让噪声听起来是「湿的」而不是白噪嘶嘶
        double lowpass = 0.0;
        // 起始音高与终止音高：380Hz → 62Hz 的快速下行扫频
        double sweepPhase = 0.0;

        for (int i = 0; i < n; i++)
        {
            double t = TimeAt(i);
            double u = t / (SquashMs / 1000.0);          // 0 → 1

            // 爆炸式的快衰减：τ = 28ms
            double env = Math.Exp(-t / 0.028);

            // 噪声经低通，截止频率随时间下移（越来越闷）
            double raw = noise.Next();
            double cutoff = 0.62 * Math.Exp(-u * 2.2) + 0.05;
            lowpass += (raw - lowpass) * cutoff;

            // 下行扫频：频率指数下滑 380 → 62
            double freq = 380.0 * Math.Pow(62.0 / 380.0, Math.Min(1.0, u * 1.15));
            sweepPhase += 2.0 * Math.PI * freq / SampleRate;
            double sweep = Math.Sin(sweepPhase);

            // 少量软削波，制造「黏糊糊被压扁」的谐波
            double mixed = 0.72 * lowpass + 0.55 * sweep * env;
            mixed = Math.Tanh(mixed * 2.6) * 0.85;

            // 起始 3ms 的点击感（爆裂瞬间）
            double click = u < 0.02 ? (1.0 - u / 0.02) * 0.35 * noise.Next() : 0.0;

            v.Samples[i] = (mixed + click) * env;
        }
        return v;
    }

    /// <summary>② 苍蝇嗡鸣：低频锯齿波 + 慢速颤音（约 400ms）。</summary>
    private static Voice BuildBuzz()
    {
        int n = (int)Math.Round(SampleRate * BuzzMs / 1000.0);
        Voice v = new(n);
        Noise noise = new(NoiseSeed ^ 0x51ED2701u);

        double phase = 0.0;
        double lowpass = 0.0;

        for (int i = 0; i < n; i++)
        {
            double t = TimeAt(i);
            double u = t / (BuzzMs / 1000.0);

            // 慢速颤音（5.7Hz）+ 更快的一层（17Hz），听起来像翅膀忽近忽远
            double vibrato = 1.0
                + 0.085 * Math.Sin(2.0 * Math.PI * 5.7 * t)
                + 0.030 * Math.Sin(2.0 * Math.PI * 17.0 * t);

            const double baseFreq = 168.0;
            phase += 2.0 * Math.PI * baseFreq * vibrato / SampleRate;
            if (phase > Math.PI * 2.0) phase -= Math.PI * 2.0;

            // 锯齿波：谐波极丰富 → 天然「烦」
            double saw = (phase / Math.PI) - 1.0;
            // 叠一个八度下的大三度，制造不和谐的拍频
            double detune = Math.Sin(phase * 1.25) * 0.35;
            // 一点带通噪声，模拟翅膀扑打的气流声
            double air = noise.Next() * 0.10;
            lowpass += (air - lowpass) * 0.25;

            // 缓慢起落（不要硬切，嗡鸣应该"挥之不去"）
            double env = Math.Min(1.0, u / 0.06) * Math.Min(1.0, (1.0 - u) / 0.18);
            v.Samples[i] = (saw * 0.55 + detune + lowpass) * env * 0.9;
        }
        return v;
    }

    /// <summary>③ 孵化：极短的低频「啵」+ 轻微噪声（约 90ms）。</summary>
    private static Voice BuildHatch()
    {
        int n = (int)Math.Round(SampleRate * HatchMs / 1000.0);
        Voice v = new(n);
        Noise noise = new(NoiseSeed ^ 0xA5A5A5A5u);

        double phase = 0.0;

        for (int i = 0; i < n; i++)
        {
            double t = TimeAt(i);
            double u = t / (HatchMs / 1000.0);

            // 「啵」：频率快速下坠 190 → 74Hz，衰减 22ms
            double freq = 190.0 * Math.Pow(74.0 / 190.0, Math.Min(1.0, u * 1.5));
            phase += 2.0 * Math.PI * freq / SampleRate;

            double pop = Math.Sin(phase) * Math.Exp(-t / 0.022);
            // 叠加二次谐波，让"黏腻"的破壳感更明显
            pop += 0.30 * Math.Sin(phase * 2.0) * Math.Exp(-t / 0.014);

            // 轻微噪声：壳碎屑
            double crackle = noise.Next() * 0.20 * Math.Exp(-t / 0.012);

            v.Samples[i] = (pop * 0.95 + crackle) * 0.95;
        }
        return v;
    }

    /// <summary>④ 繁殖：两声快速上行的短音（约 260ms）。</summary>
    private static Voice BuildBred()
    {
        int n = (int)Math.Round(SampleRate * BredMs / 1000.0);
        Voice v = new(n);

        // 两声：第一声 300 → 520Hz，第二声 460 → 780Hz，各占一半
        const double note1Start = 300.0, note1End = 520.0;
        const double note2Start = 460.0, note2End = 780.0;
        int half = n / 2;

        double phase = 0.0;
        for (int i = 0; i < n; i++)
        {
            bool second = i >= half;
            int local = second ? i - half : i;
            int localLen = second ? n - half : half;
            double u = local / (double)Math.Max(1, localLen);
            double t = local / (double)SampleRate;

            double f0 = second ? note2Start : note1Start;
            double f1 = second ? note2End : note1End;
            double freq = f0 + (f1 - f0) * u;

            phase += 2.0 * Math.PI * freq / SampleRate;
            if (phase > Math.PI * 2.0) phase -= Math.PI * 2.0;

            // 方波为主（刺、机器感），混一点三角让低频更饱满
            double square = Math.Sin(phase) >= 0 ? 1.0 : -1.0;
            double triangle = 2.0 / Math.PI * Math.Asin(Math.Sin(phase));
            double tone = square * 0.45 + triangle * 0.55;

            // 每声快速起落，两声之间留一点缝
            double env = Math.Min(1.0, u / 0.08) * Math.Min(1.0, (1.0 - u) / 0.35);
            env *= Math.Exp(-t / 0.10);

            v.Samples[i] = tone * env * 0.85;
        }
        return v;
    }

    // ==================================================================
    // WAV 编码
    // ==================================================================

    /// <summary>
    /// 16-bit 单声道 PCM + 标准 44 字节 RIFF/WAVE 头。
    /// 合成后统一归一化到 0.86 峰值，保证既不是静音也不会削顶。
    /// </summary>
    internal static byte[] EncodeWav(double[] samples, int sampleRate)
    {
        const int headerSize = 44;
        const int bitsPerSample = 16;
        int dataBytes = samples.Length * sizeof(short);

        // 归一化（只在峰值超过目标时才缩，绝不放大到失真）
        double peak = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            double a = Math.Abs(samples[i]);
            if (a > peak) peak = a;
        }
        double gain = peak > 1e-9 ? Math.Min(1.0, 0.86 / peak) : 1.0;

        byte[] buffer = new byte[headerSize + dataBytes];
        using MemoryStream ms = new(buffer, writable: true);
        using BinaryWriter w = new(ms);

        int byteRate = sampleRate * 1 * bitsPerSample / 8;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);                                   // PCM 子块大小
        w.Write((short)1);                             // PCM
        w.Write((short)1);                             // 单声道
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)2);                             // 块对齐 = 声道 × 位深/8
        w.Write((short)bitsPerSample);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);

        for (int i = 0; i < samples.Length; i++)
        {
            double value = Math.Clamp(samples[i] * gain, -1.0, 1.0);
            short pcm = (short)Math.Round(value * short.MaxValue);
            w.Write(pcm);
        }

        w.Flush();
        return buffer;
    }

    /// <summary>计算峰值与 RMS（自测用来证明「不是静音」）。</summary>
    internal static (double Peak, double Rms) Measure(byte[] wav)
    {
        if (wav.Length <= 44) return (0.0, 0.0);

        double peak = 0.0;
        double sumSq = 0.0;
        int count = (wav.Length - 44) / 2;

        for (int i = 0; i < count; i++)
        {
            int offset = 44 + i * 2;
            short pcm = (short)(wav[offset] | (wav[offset + 1] << 8));
            double value = pcm / (double)short.MaxValue;
            double a = Math.Abs(value);
            if (a > peak) peak = a;
            sumSq += value * value;
        }

        return (peak, count > 0 ? Math.Sqrt(sumSq / count) : 0.0);
    }

    /// <summary>校验 RIFF/WAVE/fmt/data 头与长度字段是否自洽。</summary>
    internal static bool IsValidWav(byte[] wav, out string reason)
    {
        reason = string.Empty;
        if (wav.Length < 44) { reason = "长度不足 44 字节"; return false; }

        string riff = System.Text.Encoding.ASCII.GetString(wav, 0, 4);
        string wave = System.Text.Encoding.ASCII.GetString(wav, 8, 4);
        string fmt = System.Text.Encoding.ASCII.GetString(wav, 12, 4);
        string data = System.Text.Encoding.ASCII.GetString(wav, 36, 4);

        if (riff != "RIFF") { reason = $"RIFF 标识是 {riff}"; return false; }
        if (wave != "WAVE") { reason = $"WAVE 标识是 {wave}"; return false; }
        if (fmt != "fmt ") { reason = $"fmt 标识是 {fmt}"; return false; }
        if (data != "data") { reason = $"data 标识是 {data}"; return false; }

        int riffSize = BitConverter.ToInt32(wav, 4);
        if (riffSize != wav.Length - 8) { reason = $"RIFF 大小 {riffSize} ≠ {wav.Length - 8}"; return false; }

        int dataSize = BitConverter.ToInt32(wav, 40);
        if (dataSize != wav.Length - 44) { reason = $"data 大小 {dataSize} ≠ {wav.Length - 44}"; return false; }

        short channels = BitConverter.ToInt16(wav, 22);
        int sampleRate = BitConverter.ToInt32(wav, 24);
        int byteRate = BitConverter.ToInt32(wav, 28);
        short bits = BitConverter.ToInt16(wav, 34);

        if (channels != 1) { reason = $"声道数 {channels}"; return false; }
        if (sampleRate != SampleRate) { reason = $"采样率 {sampleRate} ≠ {SampleRate}"; return false; }
        if (bits != 16) { reason = $"位深 {bits}"; return false; }
        if (byteRate != sampleRate * channels * bits / 8) { reason = $"byteRate {byteRate} 不一致"; return false; }

        return true;
    }

    // ==================================================================
    // winmm 互操作
    // ==================================================================

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;
    private const uint SND_PURGE = 0x0040;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    /// <summary>一段已合成并钉住内存的音频。</summary>
    private sealed class SoundBuffer
    {
        private GCHandle _handle;

        private SoundBuffer(string name, byte[] bytes, double durationMs)
        {
            Name = name;
            Bytes = bytes;
            DurationMs = durationMs;
            (double peak, double rms) = Measure(bytes);
            bool valid = IsValidWav(bytes, out _);
            Stats = new SoundStats(
                name,
                (bytes.Length - 44) / 2,
                durationMs,
                peak,
                rms,
                bytes.Length,
                valid);
        }

        public string Name { get; }
        public byte[] Bytes { get; }
        public double DurationMs { get; }
        public SoundStats Stats { get; }
        public IntPtr Pointer => _handle.AddrOfPinnedObject();

        public static SoundBuffer Create(string name, byte[] bytes, double durationMs)
            => new(name, bytes, durationMs);

        public void Pin() => _handle = GCHandle.Alloc(Bytes, GCHandleType.Pinned);

        public void Unpin()
        {
            if (_handle.IsAllocated) _handle.Free();
        }
    }
}
