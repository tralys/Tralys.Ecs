using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

[MemoryDiagnoser]
public class EnableFlipBench
{
    World world = null!;
    readonly List<Entity> live = [];
    int tick;

    [GlobalSetup]
    public void Setup()
    {
        world = Bench.World();
        for (var i = 0; i < 2000; i++)
        {
            var entity = world.Create(new PlayerBundle());
            world.Add<Stunned>(entity);
            live.Add(entity);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    /// <summary>200 enable flips applied at once, then a playback.</summary>
    [Benchmark(Baseline = true)]
    public void Outside()
    {
        Flip();
        world.Playback();
    }

    /// <summary>The same 200 flips made inside an observer, so each is recorded and applied by the playback.</summary>
    [Benchmark]
    public void Inside()
    {
        using (world.BeginDelivery())
            Flip();
        world.Playback();
    }

    void Flip()
    {
        tick++;
        for (var i = 0; i < 200; i++)
        {
            var entity = live[(tick * 31 + i * 7) % live.Count];
            world.SetEnabled<Stunned>(entity, !world.Has<Stunned>(entity));
        }
    }
}
