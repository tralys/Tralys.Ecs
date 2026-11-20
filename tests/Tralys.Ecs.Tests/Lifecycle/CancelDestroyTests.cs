namespace Tralys.Ecs.Tests;

public sealed class CancelDestroyTests
{
    [Test]
    public async Task A_cancelled_destroy_clears_the_row_s_destroy_bit()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Health { Value = 1 });

        world.Destroy(entity);
        world.CancelDestroy(entity);
        var slot = world.allocator.Slots[entity.Index];
        var archetype = slot.Archetype!;
        world.Playback();

        await Assert.That(archetype.IsPendingDestroy(slot.Row)).IsFalse();
        await Assert.That(world.StateOf(entity)).IsEqualTo(LifecycleState.Alive);
    }

    [Test]
    public async Task A_cancel_inside_a_batch_spares_a_child_of_a_pending_destroy_ancestor()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        var child = world.Create();
        world.SetParent(child, parent);
        world.Destroy(parent);

        bool cancelled;
        using (world.Batch(child))
            cancelled = world.CancelDestroy(child);
        world.Playback();

        await Assert.That(cancelled).IsTrue();
        await Assert.That(world.StateOf(child)).IsEqualTo(LifecycleState.Alive);
        await Assert.That(world.StateOf(parent)).IsEqualTo(LifecycleState.Dead);
    }

    [Test]
    public async Task A_recorded_cancel_applies_in_record_order_with_recorded_destroys()
    {
        using var world = TestWorlds.Create();
        var pending = world.Create();
        var spared = world.Create();
        var doomed = world.Create();
        world.Destroy(pending);

        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.CancelDestroy(pending);
        buffer.Commands.Destroy(spared);
        buffer.Commands.CancelDestroy(spared);
        buffer.Commands.CancelDestroy(doomed);
        buffer.Commands.Destroy(doomed);
        world.Submit(buffer);
        world.Playback();

        await Assert
            .That((world.IsLive(pending), world.IsLive(spared), world.Exists(doomed)))
            .IsEqualTo((true, true, false));
    }

    sealed class CountsDestroys : IDestroyObserver
    {
        public int Count;

        public void OnDestroy(in ObserverContext context, Entity entity) => Count++;
    }

    [Test]
    public async Task Destroy_cancel_destroy_runs_destroy_observers_once()
    {
        using var world = TestWorlds.Create();
        var observer = new CountsDestroys();
        world.Observers.OnDestroy(observer);
        var entity = world.Create();

        world.Destroy(entity);
        world.CancelDestroy(entity);
        world.Destroy(entity);
        world.Playback();

        await Assert.That(observer.Count).IsEqualTo(1);
    }

    sealed class Rescues(Entity rescued) : IDestroyObserver
    {
        public void OnDestroy(in ObserverContext context, Entity entity) =>
            context.World.CancelDestroy(rescued);
    }

    [Test]
    public async Task A_root_rescued_by_a_destroy_observer_keeps_its_children()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var child = world.Create();
        var grandchild = world.Create();
        world.SetParent(child, root);
        world.SetParent(grandchild, child);
        world.Observers.OnDestroy(new Rescues(root));

        world.Destroy(root);
        world.Playback();

        await Assert
            .That((world.StateOf(root), world.StateOf(child), world.StateOf(grandchild)))
            .IsEqualTo((LifecycleState.Alive, LifecycleState.Alive, LifecycleState.Alive));
    }

    sealed class DestroysChildThenRescues(Entity root, Entity child) : IDestroyObserver
    {
        public void OnDestroy(in ObserverContext context, Entity entity)
        {
            if (entity != root)
                return;
            context.World.Destroy(child);
            context.World.CancelDestroy(root);
        }
    }

    [Test]
    public async Task A_child_a_destroy_observer_destroys_stays_destroyed_when_its_root_is_rescued()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var child = world.Create();
        world.SetParent(child, root);
        world.Observers.OnDestroy(new DestroysChildThenRescues(root, child));

        world.Destroy(root);
        world.Playback();

        await Assert.That((world.IsLive(root), world.Exists(child))).IsEqualTo((true, false));
    }

    sealed class Orphans(Entity child) : IDestroyObserver
    {
        public void OnDestroy(in ObserverContext context, Entity entity)
        {
            if (entity == child)
                context.World.ClearParent(child);
        }
    }

    [Test]
    public async Task A_child_a_destroy_observer_takes_out_of_a_dying_subtree_survives()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var child = world.Create();
        world.SetParent(child, root);
        world.Observers.OnDestroy(new Orphans(child));

        world.Destroy(root);
        world.Playback();

        await Assert.That((world.Exists(root), world.IsLive(child))).IsEqualTo((false, true));
    }

    [Test]
    public async Task A_child_destroyed_in_the_batch_that_spares_its_parent_is_not_logged_spared()
    {
        using var world = TestWorlds.Logged();
        var parent = world.Create();
        world.Set(parent, new Position());
        var child = world.Create();
        world.Set(child, new Position());
        world.Set(child, new Health { Value = 1 });
        world.SetParent(child, parent);
        world.Destroy(parent);
        var tail = new LogTail(world);

        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.CancelDestroy(parent);
        buffer.Commands.Destroy(child);
        world.Submit(buffer);
        world.Playback(finalizeDestroys: false);

        var spared = new List<Entity>();
        foreach (var entry in tail.Read())
        {
            if (entry.Kind == LogKind.DestroyCancelled)
                spared.Add(entry.Entity);
        }
        await Assert.That(spared).IsEquivalentTo([parent]);
    }

    [Test]
    public async Task A_rescue_while_finalising_logs_the_spared_subtree()
    {
        using var world = TestWorlds.Logged();
        var root = world.Create();
        var child = world.Create();
        world.SetParent(child, root);
        world.Observers.OnDestroy(new Rescues(root));
        var tail = new LogTail(world);

        world.Destroy(root);
        world.Playback();

        var spared = new List<Entity>();
        foreach (var entry in tail.Read())
        {
            if (entry.Kind == LogKind.DestroyCancelled)
                spared.Add(entry.Entity);
        }
        await Assert.That(spared).IsEquivalentTo([root, child]);
    }

    [Test]
    public async Task A_recorded_cancel_spares_a_child_only_from_a_destroy_recorded_before_it()
    {
        using var world = TestWorlds.Create();
        var early = world.Create();
        var earlyChild = world.Create();
        world.SetParent(earlyChild, early);
        var late = world.Create();
        var lateChild = world.Create();
        world.SetParent(lateChild, late);

        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.Destroy(early);
        buffer.Commands.CancelDestroy(earlyChild);
        buffer.Commands.CancelDestroy(lateChild);
        buffer.Commands.Destroy(late);
        world.Submit(buffer);
        world.Playback();

        await Assert
            .That((world.IsLive(earlyChild), world.Parent(earlyChild), world.Exists(lateChild)))
            .IsEqualTo((true, default(Entity), false));
    }

    [Test]
    public async Task A_recorded_cancel_of_an_entity_pending_itself_leaves_it_under_its_pending_parent()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        var child = world.Create();
        world.SetParent(child, parent);
        world.Destroy(parent);
        world.Destroy(child);

        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.CancelDestroy(child);
        world.Submit(buffer);
        world.Playback(finalizeDestroys: false);

        await Assert
            .That((world.StateOf(child), world.Parent(child)))
            .IsEqualTo((LifecycleState.PendingDestroy, parent));
    }

    [Test]
    public async Task A_recorded_cancel_spares_an_entity_pending_through_its_parent()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        var child = world.Create();
        world.SetParent(child, parent);
        world.Destroy(parent);

        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.CancelDestroy(child);
        world.Submit(buffer);
        world.Playback();

        await Assert
            .That((world.Exists(parent), world.IsLive(child), world.Parent(child)))
            .IsEqualTo((false, true, default(Entity)));
    }
}
