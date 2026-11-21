using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

public class SingleEntityLookupBench
{
    readonly World world = Bench.World();
    readonly SparseSetProto<BHealth> baseline = new();
    Entity[] sequential = [];
    Entity[] shuffled = [];

    [GlobalSetup]
    public void Setup()
    {
        sequential = new Entity[10_000];
        for (var i = 0; i < sequential.Length; i++)
        {
            sequential[i] =
                i % 3 == 0 ? world.Create(new PlayerBundle()) : world.Create(new NarrowBundle());
            world.Set(sequential[i], new BHealth { Value = i });
            baseline.Add((int)sequential[i].Index, new BHealth { Value = i });
        }
        shuffled = [.. sequential];
        new Random(5).Shuffle(shuffled);
    }

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    [Benchmark(Baseline = true)]
    public long SparseSetRandom()
    {
        long sum = 0;
        foreach (var entity in shuffled)
        {
            if (baseline.Has((int)entity.Index))
                sum += baseline.Get((int)entity.Index).Value;
        }
        return sum;
    }

    [Benchmark]
    public long HandleRandom()
    {
        long sum = 0;
        foreach (var entity in shuffled)
        {
            if (world.TryGet<BHealth>(entity, out var health))
                sum += health.Value;
        }
        return sum;
    }

    [Benchmark]
    public long HandleSequential()
    {
        long sum = 0;
        foreach (var entity in sequential)
        {
            if (world.TryGet<BHealth>(entity, out var health))
                sum += health.Value;
        }
        return sum;
    }

    [Benchmark]
    public int HasRandom()
    {
        var count = 0;
        foreach (var entity in shuffled)
        {
            if (world.Has<BVelocity>(entity))
                count++;
        }
        return count;
    }
}
