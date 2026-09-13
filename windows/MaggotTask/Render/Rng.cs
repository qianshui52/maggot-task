namespace MaggotTask.Render;

/// <summary>
/// 确定性伪随机（xorshift32）。
/// 设计系统 §4：生物蠕动的相位由 seed 决定，永不使用统一时长 ——
/// 因此每只虫子用 id 派生的 seed，位置与相位都可复现。
/// </summary>
public struct Rng
{
    private uint _state;

    public Rng(uint seed)
    {
        _state = seed == 0 ? 0x9E3779B9u : seed;
    }

    public uint NextUInt()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>[0, 1)</summary>
    public double NextDouble() => (NextUInt() >> 8) * (1.0 / 16777216.0);

    /// <summary>[min, max)</summary>
    public double Range(double min, double max) => min + NextDouble() * (max - min);

    /// <summary>由 task id 派生稳定 seed。</summary>
    public static uint SeedFrom(string id)
    {
        const uint fnvOffset = 2166136261u;
        const uint fnvPrime = 16777619u;
        uint hash = fnvOffset;
        foreach (char ch in id)
        {
            hash ^= ch;
            hash *= fnvPrime;
        }
        return hash == 0 ? 0x2545F491u : hash;
    }
}
