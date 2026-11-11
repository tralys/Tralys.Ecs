namespace Tralys.Ecs.Tests;

public sealed class NestedFlushTests
{
    sealed class AddsVelocity : IAddObserver<Position>
    {
        public void OnAdd(in ObserverContext context, Entity entity, in Position value) =>
            context.World.Set(entity, new Velocity { X = value.X });
    }

    [Test]
    public async Task Commands_an_observer_queues_apply_in_the_same_playback()
    {
        using var world = TestWorlds.Create();
        world.Observers.OnAdd(new AddsVelocity());
        var entity = world.Create();
        world.Set(entity, new Position { X = 3 });

        world.Playback();

        await Assert.That(world.Has<Velocity>(entity)).IsTrue();
        await Assert.That(world.Get<Velocity>(entity).X).IsEqualTo(3f);
    }
}
