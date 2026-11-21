using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Tralys.Ecs.Benchmarks;

/// <summary>Unique indexes against a Dictionary kept by hand beside the world.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public partial class IndexBench
{
    const int Count = 10_000;
    const int Moved = 1_000;

    readonly World world = Bench.World();
    readonly Dictionary<int, Entity> byKey = [];
    Entity[] entities = [];
    int[] probes = [];
    int round;

    [GlobalSetup]
    public void Setup()
    {
        entities = new Entity[Count];
        for (var i = 0; i < Count; i++)
        {
            var entity = world.Create(new NarrowBundle());
            world.Set(entity, new BKey { Value = i });
            world.Set(entity, new BPlainKey { Value = i });
            byKey[i] = entity;
            entities[i] = entity;
        }
        probes = [.. Enumerable.Range(0, Count)];
        new Random(3).Shuffle(probes);
    }

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    [Benchmark(Baseline = true), BenchmarkCategory("Lookup 10k")]
    public long DictionaryLookup()
    {
        long sum = 0;
        foreach (var key in probes)
        {
            if (byKey.TryGetValue(key, out var entity))
                sum += entity.Index;
        }
        return sum;
    }

    [Benchmark, BenchmarkCategory("Lookup 10k")]
    public long UniqueIndexLookup()
    {
        long sum = 0;
        var index = world.BKeyByValue();
        foreach (var key in probes)
        {
            if (index.TryGet(key, out var entity))
                sum += entity.Index;
        }
        return sum;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Rekey 1k")]
    public void DictionaryRekey()
    {
        var shift = (round++ & 1) == 0 ? Count : -Count;
        for (var i = 0; i < Moved; i++)
        {
            var entity = entities[i];
            var old = world.Get<BPlainKey>(entity).Value;
            world.Set(entity, new BPlainKey { Value = old + shift });
            byKey.Remove(old);
            byKey[old + shift] = entity;
        }
    }

    [Query]
    static void Clamp(ref BKey key)
    {
        if (key.Value < 0)
            key.Value = 0;
    }

    /// <summary>A generated query taking an indexed component by <c>ref</c> over every entity, writing none of them.</summary>
    [Benchmark, BenchmarkCategory("Ref pass 10k")]
    public void IndexedRefPass() => RunClamp(world);

    [Benchmark, BenchmarkCategory("Rekey 1k")]
    public void UniqueIndexRekey()
    {
        var shift = (round++ & 1) == 0 ? Count : -Count;
        for (var i = 0; i < Moved; i++)
        {
            var entity = entities[i];
            world.Set(entity, new BKey { Value = world.Get<BKey>(entity).Value + shift });
        }
    }
}
