namespace Tralys.Ecs.Tests;

public sealed partial class ParallelRowsTests
{
    sealed partial class Scanner(World world)
    {
        [Query(Parallel = true)]
        internal void Scan(in Position position, [Arg] Entity shared)
        {
            foreach (ref readonly var effect in world.EffectRows(shared))
                _ = effect.Stacks;
        }
    }

    [Test]
    public async Task Parallel_bodies_walking_one_entity_s_rows_leave_it_marked_as_not_iterating()
    {
        using var pool = new WorkerPool();
        using var world = TestWorlds.Create(new WorldOptions { Executor = pool });
        var shared = world.Create();
        for (var kind = 0; kind < 4; kind++)
            world.EffectRows(shared).Set(new Effect { Kind = kind });
        for (var i = 0; i < 4096; i++)
            world.Set(world.Create(), new Position());
        var scanner = new Scanner(world);

        for (var run = 0; run < 64; run++)
            scanner.RunScan(world, shared);

        await Assert.That(world.FindRows<int, Effect>()?.BlockOf(shared)?.Iterating).IsEqualTo(0);
    }
}
