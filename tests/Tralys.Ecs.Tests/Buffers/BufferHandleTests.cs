namespace Tralys.Ecs.Tests;

public sealed class BufferHandleTests
{
    [Test]
    public async Task A_freed_handle_does_not_reach_the_buffer_that_reuses_its_slot()
    {
        using var world = TestWorlds.Create();
        var arena = world.Buffers<int>();
        var stale = arena.Allocate(4);
        arena.Free(stale);
        var reused = arena.Allocate(4);
        arena.Get(reused)[0] = 42;

        await Assert.That(arena.TryGet(stale, out _)).IsFalse();
    }

    [Test]
    public async Task Setting_a_component_with_a_new_handle_frees_the_old_buffer()
    {
        using var world = TestWorlds.Create();
        var arena = world.Buffers<int>();
        var entity = world.Create();
        world.Set(entity, new Position());
        var old = arena.Allocate(2);
        world.Set(entity, new Itinerary { Stops = old });
        world.Set(entity, new Itinerary { Stops = arena.Allocate(3) });

        await Assert.That((arena.TryGet(old, out _), arena.LiveCount)).IsEqualTo((false, 1));
    }

    [Test]
    public async Task An_edit_that_moves_a_handle_to_another_field_keeps_its_buffer()
    {
        using var world = TestWorlds.Create();
        var arena = world.Buffers<int>();
        var entity = world.Create();
        var queued = arena.Allocate(2);
        world.Set(entity, new Legs { Current = arena.Allocate(3), Queued = queued });

        using (var edit = world.Edit<Legs>(entity))
            edit.Value = edit.Value with { Current = queued, Queued = default };

        await Assert.That((arena.TryGet(queued, out _), arena.LiveCount)).IsEqualTo((true, 1));
    }

    [Test]
    public async Task A_dropped_handle_is_freed_though_another_buffer_type_holds_the_same_slot()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(
            entity,
            new Legs
            {
                Current = world.Buffers<int>().Allocate(2),
                Weights = world.Buffers<float>().Allocate(2),
            }
        );

        using (var edit = world.Edit<Legs>(entity))
            edit.Value.Current = default;

        await Assert
            .That((world.Buffers<int>().LiveCount, world.Buffers<float>().LiveCount))
            .IsEqualTo((0, 1));
    }

    [Test]
    public async Task Overwriting_a_row_frees_the_buffer_it_replaced()
    {
        using var world = TestWorlds.Create();
        var arena = world.Buffers<int>();
        var rows = world.LegRows(world.Create());
        rows.Set(new Leg { Slot = 1, Stops = arena.Allocate(2) });
        rows.Set(new Leg { Slot = 1, Stops = arena.Allocate(3) });

        using (var edit = rows.Edit(1))
            edit.Value.Stops = arena.Allocate(4);

        await Assert.That(arena.LiveCount).IsEqualTo(1);
    }
}
