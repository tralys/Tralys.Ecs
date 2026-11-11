namespace Tralys.Ecs.Tests;

public sealed class HandWrittenQueryTests
{
    struct CountRows : IChunkKernel
    {
        public int Rows;

        public void Execute(ref ChunkView chunk)
        {
            foreach (var row in chunk.Rows)
                Rows++;
        }
    }

    [Test]
    public async Task Rows_and_Count_skip_rows_the_query_filters_out()
    {
        using var world = TestWorlds.Create();
        foreach (var enabled in new[] { true, false })
        {
            var entity = world.Create();
            world.Set(entity, new Health { Value = 1 });
            world.Add<Frozen>(entity);
            world.SetEnabled<Frozen>(entity, enabled);
        }
        var query = world.Query(QueryDesc.Builder().Read<Health>().With<Frozen>().Build());
        var kernel = new CountRows();

        query.Run(ref kernel);

        await Assert.That((kernel.Rows, query.Count())).IsEqualTo((1, 1));
    }

    [Test]
    public async Task Equal_descriptions_are_one_description()
    {
        var first = QueryDesc.Builder().Read<Health>().Without<Frozen>().Build();
        var second = QueryDesc.Builder().Read<Health>().Without<Frozen>().Build();

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task Each_system_sees_a_change_once()
    {
        using var world = TestWorlds.Create();
        world.Set(world.Create(), new Health { Value = 1 });
        var query = world.Query(QueryDesc.Builder().Read<Health>().Changed<Health>().Build());
        var first = SystemId.Next();
        var second = SystemId.Next();

        var seen = (
            RunAs(world, query, first),
            RunAs(world, query, second),
            RunAs(world, query, first)
        );

        await Assert.That(seen).IsEqualTo((1, 1, 0));
    }

    static int RunAs(World world, CachedQuery query, SystemId system)
    {
        using var run = world.BeginSystemRun(system);
        var kernel = new CountRows();
        query.Run(ref kernel);
        return kernel.Rows;
    }

    struct Renumber(int number) : IChunkKernel
    {
        public void Execute(ref ChunkView chunk)
        {
            var badges = chunk.Write<Badge>();
            foreach (var row in chunk.Rows)
            {
                using var edit = badges.Edit(row);
                edit.Value.Number = number;
            }
        }
    }

    [Test]
    public async Task A_written_indexed_value_moves_its_index_entry()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Badge { Number = 1 });
        var kernel = new Renumber(9);

        world.Query(QueryDesc.Builder().Write<Badge>().Build()).Run(ref kernel);

        await Assert
            .That(world.BadgeByNumber().TryGet(9, out var found) && found == entity)
            .IsTrue();
    }

    [Test]
    public async Task An_edit_that_changes_nothing_stamps_nothing()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Badge { Number = 4 });
        var before = world.Changes.VersionOf<Badge>(entity);
        var kernel = new Renumber(4);

        world.Query(QueryDesc.Builder().Write<Badge>().Build()).Run(ref kernel);

        await Assert.That(world.Changes.VersionOf<Badge>(entity)).IsEqualTo(before);
    }

    struct Stops : IChunkKernel
    {
        public void Execute(ref ChunkView chunk) => chunk.Stop();
    }

    [Test]
    public async Task Stopping_a_parallel_run_throws()
    {
        using var world = TestWorlds.Create();
        world.Set(world.Create(), new Health { Value = 1 });
        var query = world.Query(QueryDesc.Builder().Read<Health>().Build());

        await Assert
            .That(() =>
            {
                var kernel = new Stops();
                query.RunParallel(ref kernel);
            })
            .Throws<InvalidOperationException>();
    }

    struct Reroute(BufferHandle<int> stops) : IChunkKernel
    {
        public void Execute(ref ChunkView chunk)
        {
            var itineraries = chunk.Write<Itinerary>();
            foreach (var row in chunk.Rows)
            {
                using var edit = itineraries.Edit(row);
                edit.Value.Stops = stops;
            }
        }
    }

    [Test]
    public async Task A_query_write_that_replaces_a_buffer_handle_frees_the_old_buffer()
    {
        using var world = TestWorlds.Create();
        var arena = world.Buffers<int>();
        var entity = world.Create();
        world.Set(entity, new Position());
        var old = arena.Allocate(2);
        world.Set(entity, new Itinerary { Stops = old });
        var kernel = new Reroute(arena.Allocate(3));

        world.Query(QueryDesc.Builder().Write<Itinerary>().Build()).Run(ref kernel);

        await Assert.That((arena.TryGet(old, out _), arena.LiveCount)).IsEqualTo((false, 1));
    }

    struct SwapLegs : IChunkKernel
    {
        public void Execute(ref ChunkView chunk)
        {
            var legs = chunk.Write<Legs>();
            foreach (var row in chunk.Rows)
            {
                using var edit = legs.Edit(row);
                edit.Value = edit.Value with
                {
                    Current = edit.Value.Queued,
                    Queued = edit.Value.Current,
                };
            }
        }
    }

    [Test]
    public async Task A_parallel_write_may_move_buffer_handles_between_fields()
    {
        using var world = TestWorlds.Create();
        var arena = world.Buffers<int>();
        var entity = world.Create();
        var current = arena.Allocate(2);
        var queued = arena.Allocate(3);
        world.Set(entity, new Legs { Current = current, Queued = queued });
        var kernel = new SwapLegs();

        world.Query(QueryDesc.Builder().Write<Legs>().Build()).RunParallel(ref kernel);
        var legs = world.Get<Legs>(entity);

        await Assert
            .That((legs.Current, legs.Queued, arena.LiveCount))
            .IsEqualTo((queued, current, 2));
    }

    [Test]
    public async Task An_edit_that_replaces_a_buffer_handle_frees_the_old_buffer()
    {
        using var world = TestWorlds.Create();
        var arena = world.Buffers<int>();
        var entity = world.Create();
        world.Set(entity, new Position());
        var old = arena.Allocate(2);
        world.Set(entity, new Itinerary { Stops = old });

        using (var edit = world.Edit<Itinerary>(entity))
            edit.Value.Stops = arena.Allocate(3);

        await Assert.That((arena.TryGet(old, out _), arena.LiveCount)).IsEqualTo((false, 1));
    }
}
