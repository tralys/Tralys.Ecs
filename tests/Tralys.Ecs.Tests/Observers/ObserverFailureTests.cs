namespace Tralys.Ecs.Tests;

public sealed class ObserverFailureTests
{
    sealed class ThrowsOnAdd : IAddObserver<Position>
    {
        public void OnAdd(in ObserverContext context, Entity entity, in Position value) =>
            throw new ApplicationException("add observer failed");
    }

    sealed class ThrowsOnDestroy : IDestroyObserver
    {
        public void OnDestroy(in ObserverContext context, Entity entity) =>
            throw new ApplicationException("destroy observer failed");
    }

    [Test]
    public async Task A_throwing_add_observer_leaves_the_world_outside_iteration()
    {
        using var world = TestWorlds.Create();
        var id = world.Observers.OnAdd(new ThrowsOnAdd());
        world.Set(world.Create(), new Position { X = 1 });

        await Assert.That(() => world.Playback()).Throws<ApplicationException>();
        world.Observers.Remove(id);

        await Assert.That(world.StateOf(world.Create())).IsEqualTo(LifecycleState.Alive);
    }

    [Test]
    public async Task A_throwing_destroy_observer_during_extract_is_reported_with_the_image()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var id = world.Observers.OnDestroy(new ThrowsOnDestroy());

        var result = world.Extract([root]);
        world.Observers.Remove(id);

        await Assert.That(result.Image.EntityCount).IsEqualTo(1);
        await Assert
            .That(result.ObserverFailures?.InnerExceptions.Single())
            .IsTypeOf<ApplicationException>();
        await Assert.That(world.Exists(root)).IsFalse();
        await Assert.That(world.StateOf(world.Create())).IsEqualTo(LifecycleState.Alive);
    }

    [Test]
    public async Task A_throwing_destroy_observer_leaves_the_world_outside_iteration()
    {
        using var world = TestWorlds.Create();
        var id = world.Observers.OnDestroy(new ThrowsOnDestroy());
        world.Destroy(world.Create());

        await Assert.That(() => world.Playback()).Throws<ApplicationException>();
        world.Observers.Remove(id);

        await Assert.That(world.StateOf(world.Create())).IsEqualTo(LifecycleState.Alive);
    }

    [Test]
    public async Task A_destroy_whose_observer_threw_is_released_by_the_playback_that_rethrows()
    {
        using var world = TestWorlds.Create();
        world.Observers.OnDestroy(new ThrowsOnDestroy());
        var entity = world.Create();
        world.Destroy(entity);

        await Assert.That(() => world.Playback()).Throws<ApplicationException>();
        await Assert.That(world.Exists(entity)).IsFalse();
    }

    sealed class CountsAdds : IAddObserver<Position>
    {
        public int Count;

        public void OnAdd(in ObserverContext context, Entity entity, in Position value) => Count++;
    }

    [Test]
    public async Task Every_add_reaches_the_other_observers_when_one_throws()
    {
        using var world = TestWorlds.Create();
        var throws = world.Observers.OnAdd(new ThrowsOnAdd());
        var counts = new CountsAdds();
        world.Observers.OnAdd(counts);
        world.Set(world.Create(), new Position { X = 1 });
        world.Set(world.Create(), new Position { X = 2 });

        await Assert.That(() => world.Playback()).Throws<AggregateException>();
        world.Observers.Remove(throws);

        await Assert.That(counts.Count).IsEqualTo(2);
    }

    sealed class CountsDestroys : IDestroyObserver
    {
        public int Count;

        public void OnDestroy(in ObserverContext context, Entity entity) => Count++;
    }

    [Test]
    public async Task Every_destroy_reaches_the_other_observers_when_one_throws()
    {
        using var world = TestWorlds.Create();
        var throws = world.Observers.OnDestroy(new ThrowsOnDestroy());
        var counts = new CountsDestroys();
        world.Observers.OnDestroy(counts);
        world.Destroy(world.Create());
        world.Destroy(world.Create());

        await Assert.That(() => world.Playback()).Throws<AggregateException>();
        world.Observers.Remove(throws);

        await Assert.That(counts.Count).IsEqualTo(2);
    }
}
