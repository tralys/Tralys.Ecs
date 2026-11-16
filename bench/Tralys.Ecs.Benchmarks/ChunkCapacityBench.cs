using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

public class ChunkCapacityBench
{
    World world = null!;
    readonly List<Entity> live = [];

    [Params(16, 32, 64)]
    public int Capacity;

    [Params("narrow", "player", "wide")]
    public string Shape = "narrow";

    [GlobalSetup]
    public void Setup()
    {
        world = Bench.World(Capacity);
        for (var i = 0; i < 4000; i++)
            live.Add(Spawn());
    }

    Entity Spawn() =>
        Shape switch
        {
            "narrow" => world.Create(new NarrowBundle()),
            "player" => world.Create(new PlayerBundle()),
            _ => world.Create(new WideBundle()),
        };

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    [Benchmark]
    public float Iterate() => Bench.SumPositions(world);

    [Benchmark]
    public void Churn()
    {
        for (var i = 0; i < 64; i++)
        {
            world.Destroy(live[i]);
            live[i] = Spawn();
        }
        world.Playback();
    }
}
