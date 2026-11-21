using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

[MemoryDiagnoser]
public partial class ManySystemsFewEntitiesBench
{
    readonly World world = Bench.World();
    Entity[] entities = [];
    SystemId[] ids = [];

    [Params(100, 1000)]
    public int Systems;

    [GlobalSetup]
    public void Setup()
    {
        entities = new Entity[64];
        for (var i = 0; i < entities.Length; i++)
            entities[i] = world.Create(new PlayerBundle());
        ids = new SystemId[Systems];
        for (var s = 0; s < ids.Length; s++)
            ids[s] = SystemId.Next();
    }

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    /// <summary>Per-system overhead: a system run, a handful of edits and a playback, with almost no entity work.</summary>
    [Benchmark]
    public void Tick()
    {
        for (var s = 0; s < Systems; s++)
        {
            using var run = world.BeginSystemRun(ids[s]);
            var entity = entities[s % entities.Length];
            using (var edit = world.Edit<BHealth>(entity))
                edit.Value.Value++;
            world.Playback();
        }
    }

    [Query]
    static void Bump(ref BHealth health) => health.Value++;

    /// <summary>The same systems as generated queries, each walking all 64 entities.</summary>
    [Benchmark]
    public void TickGenerated()
    {
        for (var s = 0; s < Systems; s++)
        {
            using var run = world.BeginSystemRun(ids[s]);
            RunBump(world);
            world.Playback();
        }
    }
}
