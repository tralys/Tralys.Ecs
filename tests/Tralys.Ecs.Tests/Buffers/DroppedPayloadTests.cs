using Tralys.Ecs.Downstream.Fixture;

namespace Tralys.Ecs.Tests;

public sealed class DroppedPayloadTests
{
    [Test]
    public async Task A_buffered_add_removed_in_the_same_batch_frees_its_buffers()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();

        using (world.BeginDelivery())
        {
            world.Set(entity, new Route { Stops = world.Buffers<int>().Allocate(2) });
            world.Remove<Route>(entity);
        }
        world.Playback();

        await Assert.That(world.Buffers<int>().LiveCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_stored_value_overwritten_then_removed_in_one_batch_frees_both_buffers()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Route { Stops = world.Buffers<int>().Allocate(2) });

        using (world.BeginDelivery())
        {
            world.Remove<Route>(entity);
            world.Set(entity, new Route { Stops = world.Buffers<int>().Allocate(3) });
            world.Remove<Route>(entity);
        }
        world.Playback();

        await Assert.That(world.Buffers<int>().LiveCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_rejected_buffered_add_frees_its_buffers()
    {
        using var world = TestWorlds.Create();
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Set(
            world.Create(),
            new Itinerary { Stops = world.Buffers<int>().Allocate(2) }
        );
        world.Submit(buffer);

        await Assert.That(() => world.Playback()).Throws<InvalidOperationException>();
        await Assert.That(world.Buffers<int>().LiveCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_create_cancelled_in_the_same_batch_frees_its_buffers()
    {
        using var world = TestWorlds.Create();

        using (world.BeginDelivery())
        {
            var created = world.Create();
            world.Set(created, new Route { Stops = world.Buffers<int>().Allocate(2) });
            world.Destroy(created);
        }
        world.Playback();

        await Assert.That(world.Buffers<int>().LiveCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_row_on_a_create_cancelled_in_the_same_batch_frees_its_buffers()
    {
        using var world = TestWorlds.Create();

        using (world.BeginDelivery())
        {
            var created = world.Create();
            world
                .LegRows(created)
                .Set(new Leg { Slot = 1, Stops = world.Buffers<int>().Allocate(2) });
            world.Destroy(created);
        }
        world.Playback();

        await Assert.That(world.Buffers<int>().LiveCount).IsEqualTo(0);
    }

    [Test]
    public async Task An_edge_to_a_create_cancelled_in_the_same_batch_frees_its_buffers()
    {
        using var world = TestWorlds.Create();
        var source = world.Create();

        using (world.BeginDelivery())
        {
            var created = world.Create();
            world.Relate(source, created, new Guides { Stops = world.Buffers<int>().Allocate(2) });
            world.Destroy(created);
        }
        world.Playback();

        await Assert.That(world.Buffers<int>().LiveCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_handle_set_again_later_in_the_batch_survives_the_sets_it_outlived()
    {
        using var world = TestWorlds.Create();
        var arena = world.Buffers<int>();
        var entity = world.Create();
        world.Set(entity, new Route());
        var kept = arena.Allocate(2);
        var dropped = arena.Allocate(3);

        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Set(entity, new Route { Stops = kept });
        buffer.Commands.Set(entity, new Route { Stops = dropped });
        buffer.Commands.Set(entity, new Route { Stops = kept });
        world.Submit(buffer);
        world.Playback();

        await Assert
            .That((arena.TryGet(kept, out _), arena.TryGet(dropped, out _)))
            .IsEqualTo((true, false));
    }
}
