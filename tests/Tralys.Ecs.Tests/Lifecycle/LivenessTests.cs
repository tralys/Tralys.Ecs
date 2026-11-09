namespace Tralys.Ecs.Tests;

public sealed class LivenessTests
{
    [Test]
    public async Task A_child_of_a_pending_root_is_pending_destroy()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var child = world.Create();
        world.SetParent(child, root);

        world.Destroy(root);

        await Assert.That(world.StateOf(child)).IsEqualTo(LifecycleState.PendingDestroy);
    }

    [Test]
    public async Task Entity_count_holds_reserved_and_pending_destroy_entities_until_finalised()
    {
        using var world = TestWorlds.Create();
        world.Create();
        var doomed = world.Create();
        using (world.BeginDelivery())
            world.Create();
        world.Destroy(doomed);
        var beforePlayback = world.EntityCount;

        world.Playback();

        await Assert.That(beforePlayback).IsEqualTo(3);
        await Assert.That(world.EntityCount).IsEqualTo(2);
    }

    [Test]
    public async Task An_inherited_tag_is_in_effect_below_the_entity_that_holds_it()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        var child = world.Create();
        world.SetParent(child, parent);

        world.Add<Disabled>(parent);

        await Assert.That(world.IsEffective<Disabled>(child)).IsTrue();
    }

    sealed class Moves : IHierarchyObserver
    {
        public (Entity Child, Entity Parent) Seen;

        public void OnHierarchy(
            in ObserverContext context,
            Entity child,
            Entity parent,
            HierarchyEvent kind
        ) => Seen = (child, parent);
    }

    [Test]
    public async Task A_hierarchy_observer_hears_the_child_before_the_parent()
    {
        using var world = TestWorlds.Create();
        var moves = new Moves();
        world.Observers.OnHierarchy(moves);
        var parent = world.Create();
        var child = world.Create();

        world.SetParent(child, parent);
        world.Playback();

        await Assert.That(moves.Seen).IsEqualTo((child, parent));
    }

    sealed class Reasons : IDestroyObserver
    {
        public readonly Dictionary<Entity, LifecycleReason> Seen = [];

        public void OnDestroy(in ObserverContext context, Entity entity) =>
            Seen[entity] = context.Reason;
    }

    [Test]
    public async Task A_cascaded_descendant_reports_the_cascade_reason()
    {
        using var world = TestWorlds.Create();
        var reasons = new Reasons();
        world.Observers.OnDestroy(reasons);
        var root = world.Create();
        var child = world.Create();
        world.SetParent(child, root);

        world.Destroy(root, new LifecycleReason(7));
        world.Playback();

        await Assert
            .That((reasons.Seen[root], reasons.Seen[child]))
            .IsEqualTo((new LifecycleReason(7), LifecycleReason.Cascade));
    }

    sealed class EdgeAndParentReasons : IRelationObserver<Follows>, IHierarchyObserver
    {
        public LifecycleReason Unrelated;
        public LifecycleReason Unparented;

        public void OnRelated(
            in ObserverContext context,
            Entity source,
            Entity target,
            in Follows payload
        ) { }

        public void OnUnrelated(
            in ObserverContext context,
            Entity source,
            Entity target,
            in Follows payload
        ) => Unrelated = context.Reason;

        public void OnRelationChanged(
            in ObserverContext context,
            Entity source,
            Entity target,
            in Follows oldPayload,
            in Follows newPayload
        ) { }

        public void OnHierarchy(
            in ObserverContext context,
            Entity child,
            Entity parent,
            HierarchyEvent kind
        )
        {
            if (kind == HierarchyEvent.ChildRemoved)
                Unparented = context.Reason;
        }
    }

    [Test]
    public async Task Edges_and_parent_links_a_destroy_releases_report_its_reason()
    {
        using var world = TestWorlds.Create();
        var reasons = new EdgeAndParentReasons();
        world.Observers.OnRelation<Follows>(reasons);
        world.Observers.OnHierarchy(reasons);
        var parent = world.Create();
        var doomed = world.Create();
        var follower = world.Create();
        world.SetParent(doomed, parent);
        world.Relate(follower, doomed, new Follows());
        world.Playback();

        world.Destroy(doomed, new LifecycleReason(7));
        world.Playback();

        await Assert
            .That((reasons.Unrelated, reasons.Unparented))
            .IsEqualTo((new LifecycleReason(7), new LifecycleReason(7)));
    }

    [Test]
    public async Task Cancelling_a_child_pending_through_its_root_spares_it_as_a_root()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var child = world.Create();
        world.SetParent(child, root);
        world.Destroy(root);

        var cancelled = world.CancelDestroy(child);
        world.Playback();

        await Assert
            .That((cancelled, world.Exists(child), world.Parent(child), world.Exists(root)))
            .IsEqualTo((true, true, default(Entity), false));
    }
}
