namespace Tralys.Ecs.Tests;

public sealed class PendingOverlayTests
{
    [Test]
    public async Task Single_entity_reads_see_the_stage_pending_remove_and_re_add()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Velocity { X = 1 });

        bool afterRemove,
            afterSet;
        float readInside;
        using (world.BeginDelivery())
        {
            world.Remove<Velocity>(entity);
            afterRemove = world.Has<Velocity>(entity);
            world.Set(entity, new Velocity { X = 7 });
            afterSet = world.Has<Velocity>(entity);
            readInside = world.Get<Velocity>(entity).X;
        }
        world.Playback();

        await Assert.That(afterRemove).IsFalse();
        await Assert.That(afterSet).IsTrue();
        await Assert.That(readInside).IsEqualTo(7f);
        await Assert.That(world.Get<Velocity>(entity).X).IsEqualTo(7f);
    }

    sealed class RewritesOnAdd : IAddObserver<Position>
    {
        public void OnAdd(in ObserverContext context, Entity entity, in Position value)
        {
            context.World.Set(entity, new Velocity { X = 2 });
            context.World.Set(entity, new Position { X = 99 });
        }
    }

    [Test]
    public async Task An_observer_write_after_a_playback_round_reaches_live_storage()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Observers.OnAdd(new RewritesOnAdd());

        using (world.BeginDelivery())
            world.Set(entity, new Position { X = 1 });
        world.Playback();

        await Assert.That(world.Get<Position>(entity).X).IsEqualTo(99f);
    }

    [Test]
    public async Task A_pending_disable_reads_as_disabled_before_playback()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();

        bool enabledInside;
        bool readInside;
        using (world.BeginDelivery())
        {
            world.Set(entity, new Burning { Ticks = 1 });
            world.SetEnabled<Burning>(entity, false);
            enabledInside = world.Has<Burning>(entity);
            readInside = world.TryGet<Burning>(entity, out _);
        }
        world.Playback();

        await Assert.That(enabledInside).IsFalse();
        await Assert.That(readInside).IsFalse();
        await Assert.That(world.Has<Burning>(entity)).IsFalse();
    }

    [Test]
    public async Task Removing_a_component_from_every_holder_includes_a_pending_add()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();

        using (world.BeginDelivery())
        {
            world.Set(entity, new Velocity { X = 1 });
            world.RemoveAll<Velocity>();
        }
        world.Playback();

        await Assert.That(world.Has<Velocity>(entity)).IsFalse();
    }

    [Test]
    public async Task Enabling_a_component_pending_removal_throws()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Burning { Ticks = 1 });

        bool threw;
        using (world.BeginDelivery())
        {
            world.Remove<Burning>(entity);
            try
            {
                world.SetEnabled<Burning>(entity, true);
                threw = false;
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
        }

        await Assert.That(threw).IsTrue();
    }
}
