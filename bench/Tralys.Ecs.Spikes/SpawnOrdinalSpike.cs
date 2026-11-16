using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Spikes;

/// <summary>D12: a stable per-tick spawn ordinal taken from the playback sort key, against spawning without one.</summary>
[MemoryDiagnoser]
public class SpawnOrdinalSpike
{
    (int Schedule, int Chunk, int Sequence)[] creates = [];
    readonly int[] ordinals = new int[4096];

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(3);
        creates = new (int, int, int)[4096];
        for (var i = 0; i < creates.Length; i++)
            creates[i] = (random.Next(8), random.Next(64), i);
    }

    [Benchmark(Baseline = true)]
    public void SortOnly() => creates.AsSpan().Sort();

    [Benchmark]
    public void SortAndAssignOrdinal()
    {
        creates.AsSpan().Sort();
        for (var i = 0; i < creates.Length; i++)
            ordinals[creates[i].Sequence] = i;
    }
}
