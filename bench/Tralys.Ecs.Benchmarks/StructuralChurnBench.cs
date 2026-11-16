using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

[MemoryDiagnoser]
public class StructuralChurnBench
{
    World world = null!;
    readonly List<Entity> live = [];
    int tick;

    [GlobalSetup]
    public void Setup()
    {
        world = Bench.World();
        for (var i = 0; i < 2000; i++)
            live.Add(world.Create(new PlayerBundle()));
    }

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    /// <summary>One steady-state tick: 100 creates, 100 destroys, 200 add/remove flips, end-of-tick playback.</summary>
    [Benchmark]
    public void Tick()
    {
        tick++;
        for (var i = 0; i < 100; i++)
            live.Add(world.Create(new PlayerBundle()));
        for (var i = 0; i < 200; i++)
        {
            var entity = live[(tick * 31 + i * 7) % live.Count];
            if (world.Has<Small8>(entity))
                world.Remove<Small8>(entity);
            else
                world.Set(entity, new Small8 { A = i });
        }
        for (var i = 0; i < 100; i++)
        {
            var at = (tick * 17 + i * 13) % live.Count;
            world.Destroy(live[at]);
            live[at] = live[^1];
            live.RemoveAt(live.Count - 1);
        }
        world.Playback();
    }
}
