namespace Tralys.Ecs.Tests;

public sealed partial class ChangedFilterTests
{
    [Query, Changed<Position>]
    static void Moved(Entity entity, [Arg] List<Entity> seen) => seen.Add(entity);

    [Test]
    public async Task A_write_after_a_changed_run_is_seen_by_the_next_run()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Position());
        RunMoved(world, []);

        world.Set(entity, new Position { X = 5 });
        var seen = new List<Entity>();
        RunMoved(world, seen);

        await Assert.That(seen).IsEquivalentTo([entity]);
    }

    struct CountChanged : IChunkKernel
    {
        public int Rows;

        public void Execute(ref ChunkView chunk)
        {
            foreach (var _ in chunk.Rows)
                Rows++;
        }
    }

    struct WriteAfterChangedRun(CachedQuery changed) : IChunkKernel
    {
        public void Execute(ref ChunkView chunk)
        {
            var health = chunk.Write<Health>();
            foreach (var row in chunk.Rows)
            {
                var nested = new CountChanged();
                changed.Run(ref nested);
                using var edit = health.Edit(row);
                edit.Value.Value++;
            }
        }
    }

    [Test]
    public async Task A_write_released_after_a_nested_changed_run_reaches_that_query_s_next_run()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Health { Value = 1 });
        var changed = world.Query(QueryDesc.Builder().Read<Health>().Changed<Health>().Build());
        var writer = world.Query(QueryDesc.Builder().Write<Health>().Build());

        int seen;
        using (world.BeginSystemRun(SystemId.Next()))
        {
            var first = new CountChanged();
            changed.Run(ref first);
            var kernel = new WriteAfterChangedRun(changed);
            writer.Run(ref kernel);
            var next = new CountChanged();
            changed.Run(ref next);
            seen = next.Rows;
        }

        await Assert.That(seen).IsEqualTo(1);
    }
}
