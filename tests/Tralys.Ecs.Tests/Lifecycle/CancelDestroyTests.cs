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
        var archetype = await Assert.That(slot.Archetype).IsNotNull();
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
        public bool Spared;

        public void OnDestroy(in ObserverContext context, Entity entity) =>
            Spared |= context.World.CancelDestroy(rescued);
    }

    [Test]
    public async Task A_destroy_observer_cannot_rescue_what_finalisation_releases()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var child = world.Create();
        world.SetParent(child, root);
        var rescues = new Rescues(root);
        world.Observers.OnDestroy(rescues);

        world.Destroy(root);
        world.Playback();

        await Assert
            .That((world.Exists(root), world.Exists(child), rescues.Spared))
            .IsEqualTo((false, false, false));
    }

    sealed class Tallies(Entity root, Entity child) : IDestroyObserver
    {
        public void OnDestroy(in ObserverContext context, Entity entity)
        {
            if (entity == root)
                context.World.TallyRows(child).Set(new Tally { Slot = 0, Count = 1 });
        }
    }

    [Test]
    public async Task A_row_a_destroy_observer_adds_to_a_finalising_entity_is_never_added()
    {
        using var world = TestWorlds.Logged();
        var root = world.Create();
        var child = world.Create();
        world.SetParent(child, root);
        world.Observers.OnDestroy(new Tallies(root, child));
        var tail = new LogTail(world);

        world.Destroy(root);
        world.Playback();

        await Assert.That(tail.Read().Any(entry => entry.Kind == LogKind.RowAdded)).IsFalse();
    }

    sealed class Adopts(Entity root, Entity adopted) : IDestroyObserver
    {
        public void OnDestroy(in ObserverContext context, Entity entity)
        {
            if (entity == root)
                context.World.SetParent(adopted, root);
        }
    }

    [Test]
    public async Task An_entity_a_destroy_observer_parents_under_a_finalising_root_goes_with_it()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var adopted = world.Create();
        world.Observers.OnDestroy(new Adopts(root, adopted));

        world.Destroy(root);
        world.Playback();

        await Assert.That((world.Exists(root), world.Exists(adopted))).IsEqualTo((false, false));
    }

    [Test]
    public async Task An_entity_a_destroy_observer_parents_under_an_extracted_root_is_left_behind()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var adopted = world.Create();
        world.Detach(root);
        world.Observers.OnDestroy(new Adopts(root, adopted));

        world.Extract([root]);

        await Assert
            .That(
                (world.Exists(adopted), world.Parent(adopted).IsDefault, world.IsDetached(adopted))
            )
            .IsEqualTo((true, true, false));
    }

    [Test]
    public async Task A_recorded_cancel_logs_each_spare_as_the_immediate_calls_do()
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
        await Assert.That(spared).IsEquivalentTo([parent, child]);
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

    sealed class Reasons : IDestroyObserver
    {
        public readonly Dictionary<Entity, LifecycleReason> Seen = [];

        public void OnDestroy(in ObserverContext context, Entity entity) =>
            Seen[entity] = context.Reason;
    }

    [Test]
    public async Task A_recorded_cancel_then_destroy_takes_the_new_reason_as_the_immediate_calls_do()
    {
        using var world = TestWorlds.Create();
        var reasons = new Reasons();
        world.Observers.OnDestroy(reasons);
        var immediate = world.Create();
        var recorded = world.Create();
        world.Destroy(immediate, new LifecycleReason(7));
        world.Destroy(recorded, new LifecycleReason(7));

        using (world.BeginDelivery())
            world.Set(recorded, new Position { X = 1 });
        world.CancelDestroy(immediate);
        world.Destroy(immediate, new LifecycleReason(9));
        world.CancelDestroy(recorded);
        world.Destroy(recorded, new LifecycleReason(9));
        world.Playback();

        await Assert
            .That((reasons.Seen[immediate], reasons.Seen[recorded]))
            .IsEqualTo((new LifecycleReason(9), new LifecycleReason(9)));
    }

    [Test]
    public async Task A_second_recorded_cancel_spares_nothing_as_a_second_immediate_one_does()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Destroy(entity);
        using (world.BeginDelivery())
            world.Set(entity, new Position { X = 1 });

        var first = world.CancelDestroy(entity);
        var second = world.CancelDestroy(entity);

        await Assert.That((first, second)).IsEqualTo((true, false));
    }

    [Test]
    public async Task A_walk_s_cancel_does_not_claim_a_parallel_destroy_that_lands_after_it()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Velocity { X = 0 });
        world.BeginSystemRun(SystemId.Next()).Dispose();
        var parallel = world.RentBuffer(world.runSequence, 0);
        parallel.Commands.Destroy(entity);
        world.Submit(parallel);
        world.BeginSystemRun(SystemId.Next()).Dispose();

        bool cancelled;
        var query = world.Query(QueryDesc.Builder().Read<Velocity>().Build());
        using (var walk = query.GetEnumerator())
        {
            walk.MoveNext();
            cancelled = world.CancelDestroy(entity);
        }
        world.Playback();

        await Assert.That((cancelled, world.IsLive(entity))).IsEqualTo((false, false));
    }

    [Test]
    public async Task Cancelling_a_destroy_counts_as_a_structural_change_with_no_watch_registered()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Destroy(entity);
        var before = world.Introspection.StructuralChanges;

        world.CancelDestroy(entity);

        await Assert.That(world.Introspection.StructuralChanges).IsGreaterThan(before);
    }
}
