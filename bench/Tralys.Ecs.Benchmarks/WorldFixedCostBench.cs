using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

[MemoryDiagnoser]
public class WorldFixedCostBench
{
    [Benchmark]
    public World EmptyWorld() => World.Create(Bench.Registry);

    [Benchmark]
    public int TwoThousandWorlds()
    {
        var worlds = new World[2000];
        for (var i = 0; i < worlds.Length; i++)
            worlds[i] = World.Create(Bench.Registry);
        return worlds.Length;
    }

    /// <summary>Bytes a tracked type costs per row: one changed and one added 64-bit stamp.</summary>
    public static int StampBytesPerRowPerTrackedType => 2 * sizeof(ulong);
}
