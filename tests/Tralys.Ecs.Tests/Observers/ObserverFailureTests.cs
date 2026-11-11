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

    sealed class Redestroys(Entity target) : IDestroyObserver
    {
        public void OnDestroy(in ObserverContext context, Entity entity)
        {
            if (entity != target)
                return;
            context.World.CancelDestroy(target);
            context.World.Destroy(target);
        }
    }

    [Test]
    public async Task A_child_destroyed_again_by_an_observer_stays_pending_when_finalising_fails()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var child = world.Create();
        world.SetParent(child, root);
        world.Destroy(root);
        var redestroys = world.Observers.OnDestroy(new Redestroys(child));
        var throws = world.Observers.OnDestroy(new ThrowsOnDestroy());

        await Assert.That(() => world.Playback()).Throws<AggregateException>();
        world.Observers.Remove(redestroys);
        world.Observers.Remove(throws);

        await Assert
            .That((world.StateOf(child), world.CancelDestroy(child)))
            .IsEqualTo((LifecycleState.PendingDestroy, true));
    }

    sealed class Rescues(Entity rescued) : IDestroyObserver
    {
        public void OnDestroy(in ObserverContext context, Entity entity) =>
            context.World.CancelDestroy(rescued);
    }

    [Test]
    public async Task A_child_of_a_rescued_root_is_back_in_its_indexes_when_finalising_fails()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var child = world.Create();
        world.Set(child, new Badge { Number = 5 });
        world.SetParent(child, root);
        world.Destroy(root);
        var rescues = world.Observers.OnDestroy(new Rescues(root));
        var throws = world.Observers.OnDestroy(new ThrowsOnDestroy());

        await Assert.That(() => world.Playback()).Throws<AggregateException>();
        world.Observers.Remove(rescues);
        world.Observers.Remove(throws);
        world.BadgeByNumber().TryGet(5, out var found);

        await Assert.That(found).IsEqualTo(child);
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
    public async Task A_throwing_destroy_observer_during_extract_leaves_the_world_outside_iteration()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var id = world.Observers.OnDestroy(new ThrowsOnDestroy());

        await Assert.That(() => world.Extract([root])).Throws<ApplicationException>();
        world.Observers.Remove(id);

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
    public async Task A_destroy_whose_observer_threw_finalises_at_the_next_playback()
    {
        using var world = TestWorlds.Create();
        var id = world.Observers.OnDestroy(new ThrowsOnDestroy());
        var entity = world.Create();
        world.Destroy(entity);

        await Assert.That(() => world.Playback()).Throws<ApplicationException>();
        world.Observers.Remove(id);
        world.Playback();

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
