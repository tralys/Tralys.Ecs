using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

[MemoryDiagnoser]
public class PlaybackBench
{
    readonly World world = Bench.World();
    Entity[] entities = [];

    [Params(4, 32)]
    public int Chunks;

    [GlobalSetup]
    public void Setup()
    {
        entities = new Entity[1024];
        for (var i = 0; i < entities.Length; i++)
            entities[i] = world.Create(new NarrowBundle());
    }

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    /// <summary>1024 value sets and 512 add/remove flips recorded across chunk buffers, then one playback.</summary>
    [Benchmark]
    public void RecordAndPlay()
    {
        var perChunk = entities.Length / Chunks;
        for (var c = 0; c < Chunks; c++)
        {
            var buffer = world.RentBuffer(1, c);
            var commands = buffer.Commands;
            for (var i = c * perChunk; i < (c + 1) * perChunk; i++)
            {
                commands.Set(entities[i], new BPosition { X = i });
                if ((i & 1) == 0)
                {
                    if (world.Has<BVelocity>(entities[i]))
                        commands.Remove<BVelocity>(entities[i]);
                    else
                        commands.Set(entities[i], new BVelocity { X = 1 });
                }
            }
            world.Submit(buffer);
        }
        world.Playback();
    }
}
