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
    public async Task Finalising_spares_a_root_whose_cascade_reaches_a_throw_target_and_reports_it_once()
    {
        using var world = TestWorlds.Create();
        var ship = world.Create();
        var crew = world.Create();
        world.Relate(crew, ship, new CrewOf());
        world.Destroy(ship);
        world.Relate(world.Create(), crew, new Escorts());

        await Assert.That(() => world.Playback()).Throws<TargetPolicyException>();
        world.Playback();
        await Assert
            .That((world.StateOf(ship), world.StateOf(crew)))
            .IsEqualTo((LifecycleState.Alive, LifecycleState.Alive));
    }

    sealed class Enlists(Entity ship, Entity crew) : IDestroyObserver
    {
        public void OnDestroy(in ObserverContext context, Entity entity)
        {
            if (entity == ship)
                context.World.Relate(crew, ship, new CrewOf());
        }
    }

    [Test]
    public async Task An_edge_a_destroy_observer_makes_onto_a_finalising_target_gets_the_target_s_policy()
    {
        using var world = TestWorlds.Create();
        var ship = world.Create();
        var crew = world.Create();
        world.Observers.OnDestroy(new Enlists(ship, crew));

        world.Destroy(ship);
        world.Playback();

        await Assert.That((world.Exists(ship), world.Exists(crew))).IsEqualTo((false, false));
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

    static void ReparentAfterDestroy(World world, Entity root, Entity child)
    {
        using (world.Batch(child))
        {
            using (world.Batch(root))
            {
                world.Destroy(root);
                world.SetParent(child, root);
            }
        }
    }

    [Test]
    public async Task A_batch_s_destroy_waits_for_a_reparent_recorded_after_it_and_is_judged_with_it()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var escorted = world.Create();
        world.Relate(world.Create(), escorted, new Escorts());

        ReparentAfterDestroy(world, root, escorted);

        await Assert.That(() => world.Playback()).Throws<TargetPolicyException>();
        await Assert
            .That((world.StateOf(root), world.Parent(escorted)))
            .IsEqualTo((LifecycleState.Alive, root));
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
