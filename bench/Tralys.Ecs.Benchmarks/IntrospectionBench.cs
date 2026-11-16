using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

/// <summary>A tick-path sample of every introspection read over 1,000 entities in 16 archetypes with a cursor and a query: counts, fill,
/// archetype and query walks, log stats and one entity visit.</summary>
[MemoryDiagnoser]
public partial class IntrospectionBench
{
    World world = null!;
    Entity sample;
    int touched;

    [Query]
    static void Touch(in BPosition position, [Arg] IntrospectionBench self) => self.touched++;

    struct Counter : IComponentVisitor
    {
        public int Seen;

        public void Visit<T>(ComponentDescriptor descriptor, in T value)
            where T : struct => Seen++;
    }

    [GlobalSetup]
    public void Setup()
    {
        world = Bench.World();
        world.Changes.Register("probe");
        for (var i = 0; i < 1_000; i++)
        {
            var entity = world.Create();
            world.Set(entity, new BPosition { X = i });
            if ((i & 1) != 0)
                world.Set(entity, new W0 { A = i });
            if ((i & 2) != 0)
                world.Set(entity, new W1 { A = i });
            if ((i & 4) != 0)
                world.Set(entity, new W2 { A = i });
            if ((i & 8) != 0)
                world.Set(entity, new W3 { A = i });
            sample = entity;
        }
        RunTouch(world, this);
    }

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    [Benchmark]
    public long Read()
    {
        var introspection = world.Introspection;
        long total = introspection.ArchetypeCount + introspection.ChunkCount;
        total += (long)(introspection.MeanChunkFill * 100);
        foreach (var archetype in introspection.Archetypes)
            total += archetype.EntityCount + archetype.BytesPerChunk + (long)(archetype.Fill * 100);
        foreach (var query in introspection.Queries)
            total += query.MatchedEntities;
        total += introspection.Log.Length + introspection.ChunksVisited;
        var counter = new Counter();
        introspection.VisitEntity(sample, ref counter);
        return total + counter.Seen;
    }
}
