using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Spikes;

/// <summary>D12: staggered per-entity update buckets as enable bits plus cadence, against a frequency term tested per entity.</summary>
public class UpdateFrequencySpike
{
    const int Entities = 8192;
    const int Buckets = 4;
    readonly float[] values = new float[Entities];
    readonly ulong[] enabled = new ulong[Entities / 64];
    readonly int[] frequency = new int[Entities];
    int tick;

    [GlobalSetup]
    public void Setup()
    {
        for (var i = 0; i < Entities; i++)
            frequency[i] = i % Buckets == 0 ? 1 : Buckets;
    }

    [Benchmark(Baseline = true)]
    public void EnableBitsPlusCadence()
    {
        tick++;
        var bucket = tick % Buckets;
        for (var w = 0; w < enabled.Length; w++)
        {
            var mask = 0UL;
            for (var b = 0; b < 64; b++)
            {
                var i = (w * 64) + b;
                if (frequency[i] == 1 || i % Buckets == bucket)
                    mask |= 1UL << b;
            }
            enabled[w] = mask;
        }
        for (var w = 0; w < enabled.Length; w++)
        {
            var bits = enabled[w];
            while (bits != 0)
            {
                var b = System.Numerics.BitOperations.TrailingZeroCount(bits);
                values[(w * 64) + b] += 1f;
                bits &= bits - 1;
            }
        }
    }

    [Benchmark]
    public void FrequencyTermPerEntity()
    {
        tick++;
        for (var i = 0; i < Entities; i++)
        {
            if (tick % frequency[i] == i % frequency[i])
                values[i] += 1f;
        }
    }
}
