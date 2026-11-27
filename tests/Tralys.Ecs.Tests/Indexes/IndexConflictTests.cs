namespace Tralys.Ecs.Tests;

public sealed partial class IndexConflictTests
{
    [Query(Parallel = true)]
    static void Respawn(Entity entity, in Serial serial, Commands commands)
    {
        var spawned = commands.Create();
        commands.Set(spawned, new Serial { Code = serial.Code });
        commands.Destroy(entity);
    }

    [Test]
    public async Task A_conflict_from_CancelDestroy_is_thrown_after_its_children_are_restored()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        world.Set(parent, new Serial { Code = 1 });
        var child = world.Create();
        world.Set(child, new Lot { Code = 7 });
        world.SetParent(child, parent);
        world.Destroy(parent);
        var respawned = world.Create();
        world.Set(respawned, new Serial { Code = 1 });

        IndexConflictException? conflict = null;
        try
        {
            world.CancelDestroy(parent);
        }
        catch (IndexConflictException error)
        {
            conflict = error;
        }
        world.LotByCode().TryGet(7, out var restored);

        await Assert.That(conflict?.Holder).IsEqualTo(respawned);
        await Assert.That(conflict?.Incoming).IsEqualTo(parent);
        await Assert.That(restored).IsEqualTo(child);
    }

    [Test]
    public async Task A_conflict_is_thrown_together_with_a_finalisation_failure_of_the_same_playback()
    {
        using var world = TestWorlds.Create();
        var ship = world.Create();
        var crew = world.Create();
        world.Relate(crew, ship, new CrewOf());
        world.Destroy(ship);
        world.Relate(world.Create(), crew, new Escorts());
        world.Set(world.Create(), new Serial { Code = 1 });
        var rival = world.Create();
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Set(rival, new Serial { Code = 1 });
        world.Submit(buffer);

        AggregateException? failure = null;
        try
        {
            world.Playback();
        }
        catch (AggregateException error)
        {
            failure = error;
        }

        await Assert
            .That(failure?.InnerExceptions.Select(e => e.GetType()).ToArray())
            .IsEquivalentTo([typeof(TargetPolicyException), typeof(IndexConflictException)]);
    }

    [Test]
    public async Task A_key_handed_to_a_respawn_within_one_playback_is_no_conflict()
    {
        using var world = TestWorlds.Create();
        var original = world.Create();
        world.Set(original, new Serial { Code = 42 });

        RunRespawn(world);
        world.Playback();
        world.SerialByCode().TryGet(42, out var holder);

        await Assert.That(holder).IsNotEqualTo(original);
        await Assert.That(world.StateOf(holder)).IsEqualTo(LifecycleState.Alive);
    }
}
