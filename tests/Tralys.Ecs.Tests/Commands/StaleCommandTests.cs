namespace Tralys.Ecs.Tests;

public sealed class StaleCommandTests
{
    [Test]
    public async Task A_command_to_a_handle_that_dies_before_playback_skips_the_slot_s_new_entity()
    {
        using var world = TestWorlds.Create();
        var old = world.Create();
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Set(old, new Health { Value = 3 });
        world.Destroy(old);
        world.Playback();
        var reuser = world.Create();

        world.Submit(buffer);
        world.Playback();

        await Assert.That(reuser.Index).IsEqualTo(old.Index);
        await Assert.That(world.Has<Health>(reuser)).IsFalse();
        await Assert.That(world.Introspection.LastPlayback.StaleSkipped).IsEqualTo(1);
    }

    [Test]
    public async Task A_parallel_command_to_a_dead_handle_throws_at_the_call()
    {
        using var world = TestWorlds.Create();
        var dead = world.Create();
        world.Destroy(dead);
        world.Playback();
        var buffer = world.RentBuffer(1, 0);

        await Assert
            .That(() => buffer.Commands.Set(dead, new Health { Value = 3 }))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_placeholder_kept_past_its_playback_is_rejected_by_the_next_buffer()
    {
        using var world = TestWorlds.Create();
        var buffer = world.RentBuffer(1, 0);
        var kept = buffer.Commands.Create();
        world.Submit(buffer);
        world.Playback();
        var reused = world.RentBuffer(1, 0);

        await Assert.That(() => reused.Commands.Destroy(kept)).Throws<InvalidOperationException>();
    }
}
