using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

/// <summary>Rewinding a test world to its baseline, against clearing it and building the baseline again. Entities spread over 16
/// archetypes, each with a unique key; every tenth follows another and every fifth has a parent.</summary>
[MemoryDiagnoser]
public class ResetBench
{
    readonly World world = Bench.World();
    WorldSnapshot? baseline;

    [Params(1_000, 10_000)]
    public int Entities;

    [GlobalSetup]
    public void Setup()
    {
        Build(world, Entities);
        baseline = world.Snapshot();
    }

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    static void Build(World world, int count)
    {
        var made = new Entity[count];
        for (var i = 0; i < count; i++)
        {
            var entity = world.Create();
            made[i] = entity;
            world.Set(entity, new BKey { Value = i });
            world.Set(entity, new BPosition { X = i });
            if ((i & 1) != 0)
                world.Set(entity, new W0 { A = i });
            if ((i & 2) != 0)
                world.Set(entity, new W1 { A = i });
            if ((i & 4) != 0)
                world.Set(entity, new W2 { A = i });
            if ((i & 8) != 0)
                world.Set(entity, new W3 { A = i });
            if (i % 10 == 9)
                world.Relate<BFollows>(entity, made[i - 1]);
            if (i % 5 == 4)
                world.SetParent(entity, made[i - 4]);
        }
    }

    [Benchmark(Baseline = true)]
    public void ClearAndRebuild()
    {
        world.Clear();
        Build(world, Entities);
    }

    [Benchmark]
    public void Reset() =>
        world.Reset(
            baseline
                ?? throw new InvalidOperationException(
                    "Setup takes the baseline at the Entities under test."
                )
        );
}
