namespace Tralys.Ecs.Tests;

public sealed class TargetPolicyTests
{
    [Test]
    public async Task Destroying_an_ancestor_of_a_throw_target_throws()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        var target = world.Create();
        world.SetParent(target, parent);
        world.Relate(world.Create(), target, new Escorts());

        await Assert.That(() => world.Destroy(parent)).Throws<InvalidOperationException>();
        await Assert.That(world.IsLive(target)).IsTrue();
    }

    [Test]
    public async Task Moving_a_throw_target_under_a_dying_parent_throws()
    {
        using var world = TestWorlds.Create();
        var dying = world.Create();
        var target = world.Create();
        world.Relate(world.Create(), target, new Escorts());
        world.Destroy(dying);

        await Assert.That(() => world.SetParent(target, dying)).Throws<InvalidOperationException>();
        await Assert.That(world.Parent(target)).IsEqualTo(default(Entity));
    }

    [Test]
    public async Task Relating_to_a_child_of_a_dying_parent_throws()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        var child = world.Create();
        world.SetParent(child, parent);
        world.Destroy(parent);

        await Assert
            .That(() => world.Relate(world.Create(), child, new Escorts()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_throw_source_destroyed_with_its_target_does_not_block_the_destroy()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        var source = world.Create();
        var target = world.Create();
        world.SetParent(source, parent);
        world.SetParent(target, parent);
        world.Relate(source, target, new Escorts());

        world.Destroy(parent);
        world.Playback();

        await Assert.That(world.Exists(target)).IsFalse();
    }

    [Test]
    public async Task Finalising_refuses_a_cascade_onto_a_throw_target_and_releases_nothing()
    {
        using var world = TestWorlds.Create();
        var ship = world.Create();
        var crew = world.Create();
        world.Relate(crew, ship, new CrewOf());
        world.Destroy(ship);
        world.Relate(world.Create(), crew, new Escorts());

        await Assert.That(() => world.Playback()).Throws<InvalidOperationException>();
        await Assert
            .That((world.StateOf(ship), world.StateOf(crew)))
            .IsEqualTo((LifecycleState.PendingDestroy, LifecycleState.Alive));
    }

    sealed class Spares(Entity spared) : IDestroyObserver
    {
        public void OnDestroy(in ObserverContext context, Entity entity) =>
            context.World.CancelDestroy(spared);
    }

    [Test]
    public async Task A_source_spared_by_a_destroy_observer_keeps_its_target_alive()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var target = world.Create();
        var source = world.Create();
        world.SetParent(target, root);
        world.Relate(source, target, new Escorts());
        world.Destroy(source);
        world.Destroy(root);
        var spares = world.Observers.OnDestroy(new Spares(source));

        await Assert.That(() => world.Playback()).Throws<InvalidOperationException>();
        world.Observers.Remove(spares);
        world.CancelDestroy(root);

        await Assert.That(world.IsLive(target)).IsTrue();
    }

    [Test]
    public async Task A_child_spared_by_a_destroy_observer_keeps_its_dying_parent_target_alive()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var child = world.Create();
        world.SetParent(child, root);
        world.Relate(child, root, new Escorts());
        world.Destroy(root);
        var spares = world.Observers.OnDestroy(new Spares(child));

        await Assert.That(() => world.Playback()).Throws<InvalidOperationException>();
        world.Observers.Remove(spares);

        await Assert.That(world.Exists(root)).IsTrue();
    }

    [Test]
    public async Task A_throw_source_that_only_a_cascade_destroys_blocks_its_target()
    {
        using var world = TestWorlds.Create();
        var ship = world.Create();
        var crew = world.Create();
        world.Relate(crew, ship, new CrewOf());
        world.Relate(crew, ship, new Escorts());

        await Assert.That(() => world.Destroy(ship)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_throw_source_released_by_the_same_cascade_as_its_target_does_not_block()
    {
        using var world = TestWorlds.Create();
        var ship = world.Create();
        var crew = world.Create();
        var mate = world.Create();
        world.SetParent(mate, crew);
        world.Relate(crew, ship, new CrewOf());
        world.Relate(mate, crew, new Escorts());

        world.Destroy(ship);
        world.Playback();

        await Assert.That((world.Exists(crew), world.Exists(mate))).IsEqualTo((false, false));
    }

    [Test]
    public async Task An_entity_created_this_stage_with_a_live_throw_source_is_not_silently_cancelled()
    {
        using var world = TestWorlds.Create();
        var source = world.Create();
        Entity created;
        using (world.BeginDelivery())
        {
            created = world.Create();
            world.Relate(source, created, new Escorts());
        }
        world.Destroy(source);
        world.Destroy(created);
        world.CancelDestroy(source);

        await Assert.That(() => world.Playback()).Throws<InvalidOperationException>();
        await Assert.That(world.Targets<Escorts>(source).Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_recorded_destroy_is_guarded_against_the_reparents_recorded_with_it()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        var moved = world.Create();
        var kept = world.Create();
        world.SetParent(moved, parent);
        world.Relate(world.Create(), moved, new Escorts());
        world.Relate(world.Create(), kept, new Escorts());
        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.ClearParent(moved);
        buffer.Commands.Destroy(parent);
        buffer.Commands.SetParent(kept, parent);
        world.Submit(buffer);

        await Assert.That(() => world.Playback()).Throws<InvalidOperationException>();
        await Assert.That((world.IsLive(parent), world.Parent(kept))).IsEqualTo((true, parent));
    }

    static void DestroyInBatch(World world, Entity entity)
    {
        using (world.Batch(entity))
            world.Destroy(entity);
    }

    [Test]
    public async Task A_destroy_in_an_open_batch_is_guarded_when_the_batch_closes()
    {
        using var world = TestWorlds.Create();
        var target = world.Create();
        world.Relate(world.Create(), target, new Escorts());

        await Assert.That(() => DestroyInBatch(world, target)).Throws<TargetPolicyException>();
        await Assert.That(world.IsLive(target)).IsTrue();
    }

    [Test]
    public async Task A_recorded_destroy_is_guarded_again_once_a_later_one_is_spared()
    {
        using var world = TestWorlds.Create();
        var first = world.Create();
        world.Set(first, new Health { Value = 1 });
        var second = world.Create();
        world.Set(second, new Position());
        world.Relate(second, first, new Escorts());
        world.Relate(world.Create(), second, new Escorts());
        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.Destroy(first);
        buffer.Commands.Destroy(second);
        world.Submit(buffer);

        await Assert.That(() => world.Playback()).Throws<Exception>();
        await Assert.That((world.IsLive(first), world.IsLive(second))).IsEqualTo((true, true));
    }
}
