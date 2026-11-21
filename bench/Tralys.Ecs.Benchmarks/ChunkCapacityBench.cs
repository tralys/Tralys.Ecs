using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

public class ChunkCapacityBench
{
    World? world;
    readonly List<Entity> live = [];

    [Params(16, 32, 64)]
    public int Capacity;

    [Params("narrow", "player", "wide")]
    public string Shape = "narrow";

    World Built =>
        world
        ?? throw new InvalidOperationException(
            "Setup builds the world at the Capacity under test."
        );

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
            "narrow" => Built.Create(new NarrowBundle()),
            "player" => Built.Create(new PlayerBundle()),
            _ => Built.Create(new WideBundle()),
        };

    [GlobalCleanup]
    public void Cleanup() => world?.Dispose();

    [Benchmark]
    public float Iterate() => Bench.SumPositions(Built);

    [Benchmark]
    public void Churn()
    {
        for (var i = 0; i < 64; i++)
        {
            Built.Destroy(live[i]);
            live[i] = Spawn();
        }
        Built.Playback();
    }
}
