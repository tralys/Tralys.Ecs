using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

/// <summary>A generated sweep over every timer against the ordered index, by how many of 10k timers fall due each tick. Each due timer is
/// rescheduled a full cycle later, so the sweep's cost stays fixed while the index's follows the due count.</summary>
[MemoryDiagnoser]
public partial class DueRatioBench
{
    const int Count = 10_000;

    World world = null!;
    double now;

    [Params(10, 100, 1000)]
    public int DuePerTick;

    [GlobalSetup]
    public void Setup()
    {
        world = Bench.World();
        for (var i = 0; i < Count; i++)
        {
            var entity = world.Create();
            world.Set(entity, new BDue { At = i });
            world.Set(entity, new BPlainDue { At = i });
        }
    }

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    [Benchmark(Baseline = true)]
    public void SweepEveryTimer()
    {
        now += DuePerTick;
        RunReschedule(world, now);
    }

    [Benchmark]
    public void OrderedIndexDue()
    {
        now += DuePerTick;
        foreach (var entity in world.BDueDue().Due(now))
            world.Set(entity, new BDue { At = world.Get<BDue>(entity).At + Count });
    }

    [Query]
    static void Reschedule(ref BPlainDue due, [Arg] double now)
    {
        if (due.At <= now)
            due.At += Count;
    }
}
