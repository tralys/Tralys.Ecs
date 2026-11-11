namespace Tralys.Ecs.Tests;

public sealed class CommandPlaybackTests
{
    [Test]
    public async Task Each_enable_toggle_of_a_missing_type_is_a_listed_rejection()
    {
        using var world = TestWorlds.Create();
        var first = world.Create();
        var second = world.Create();
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.SetEnabled<Frozen>(first, true);
        buffer.Commands.SetEnabled<Frozen>(second, true);
        world.Submit(buffer);

        var rejected = Assert.Throws<CommandRejectedException>(() => world.Playback());

        await Assert.That(rejected.Rejections.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Playback_inside_an_iteration_throws_before_playing()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();

        await Assert.That(() => PlayInside(world, entity)).Throws<InvalidOperationException>();
        world.Playback();
        await Assert.That(world.Introspection.LastPlayback.Applied).IsEqualTo(1);
    }

    static void PlayInside(World world, Entity entity)
    {
        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.Set(entity, new Health { Value = 1 });
        world.Submit(buffer);
        using var iteration = world.BeginIteration();
        world.Playback();
    }

    [Test]
    public async Task Recorded_relation_edits_apply_at_playback()
    {
        using var world = TestWorlds.Create();
        var source = world.Create();
        var oldTarget = world.Create();
        var newTarget = world.Create();
        var cleared = world.Create();
        var follower = world.Create();
        world.Relate<Follows>(source, oldTarget);
        world.Relate<Follows>(cleared, oldTarget);
        world.Relate<Follows>(follower, newTarget);
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Retarget<Follows>(source, oldTarget, newTarget);
        buffer.Commands.ClearTargets<Follows>(cleared);
        buffer.Commands.ClearSources<Follows>(newTarget);
        world.Submit(buffer);

        world.Playback();

        await Assert
            .That(
                (
                    world.HasRelation<Follows>(source, oldTarget),
                    world.HasRelation<Follows>(cleared, oldTarget),
                    world.Sources<Follows>(newTarget).Count
                )
            )
            .IsEqualTo((false, false, 0));
    }

    [Test]
    public async Task A_recorded_retarget_keeps_the_moved_edge_s_payload()
    {
        using var world = TestWorlds.Create();
        var source = world.Create();
        var first = world.Create();
        var moved = world.Create();
        world.Relate(source, first, new Owes { Amount = 1 });
        world.Relate(source, world.Create(), new Owes { Amount = 2 });
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Retarget<Owes>(source, first, moved);
        world.Submit(buffer);

        world.Playback();

        world.TryGetRelation<Owes>(source, moved, out var owed);
        await Assert.That(owed.Amount).IsEqualTo(1);
    }
}
