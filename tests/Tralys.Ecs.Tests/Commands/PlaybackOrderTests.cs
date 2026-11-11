namespace Tralys.Ecs.Tests;

public sealed class PlaybackOrderTests
{
    [Test]
    public async Task Buffers_play_back_by_chunk_order_whatever_order_they_arrive_in()
    {
        await Assert.That(Run(shuffled: true)).IsEqualTo(Run(shuffled: false));
    }

    [Test]
    public async Task Stage_commands_from_two_runs_straddle_a_parallel_buffer_recorded_between_them()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Velocity { X = 0 });

        world.BeginSystemRun(SystemId.Next()).Dispose();
        using (world.BeginDelivery())
            world.Remove<Velocity>(entity);
        world.BeginSystemRun(SystemId.Next()).Dispose();
        var parallel = world.RentBuffer(world.runSequence, 0);
        parallel.Commands.Set(entity, new Velocity { X = 2 });
        world.Submit(parallel);
        world.BeginSystemRun(SystemId.Next()).Dispose();
        using (world.BeginDelivery())
            world.Set(entity, new Velocity { X = 3 });
        world.Playback();

        await Assert.That(world.Get<Velocity>(entity).X).IsEqualTo(3);
    }

    [Test]
    public async Task A_replacement_is_logged_in_operation_order_among_the_playbacks_other_changes()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Medal { Rank = 1 });
        var cursor = world.Changes.Register("log");

        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Set(entity, new Velocity { X = 1 });
        buffer.Commands.Set(entity, new Medal { Rank = 2 });
        world.Submit(buffer);
        world.Playback();

        var kinds = new List<LogKind>();
        foreach (var entry in world.Changes.Sample(cursor).Log)
            kinds.Add(entry.Kind);
        await Assert.That(string.Join(" ", kinds)).IsEqualTo("Added Replaced");
    }

    static string Run(bool shuffled)
    {
        using var world = TestWorlds.Create();
        var cursor = world.Changes.Register("log");
        var moved = world.Create();
        world.Set(moved, new Position());
        var toggled = world.Create();

        var first = world.RentBuffer(1, 0);
        var second = world.RentBuffer(1, 1);
        var third = world.RentBuffer(1, 2);
        first.Commands.Set(moved, new Position { X = 1 });
        second.Commands.Set(moved, new Position { X = 2 });
        third.Commands.Set(moved, new Position { X = 3 });
        first.Commands.Set(toggled, new Velocity { X = 1 });
        second.Commands.Remove<Velocity>(toggled);
        foreach (var buffer in shuffled ? new[] { third, first, second } : [first, second, third])
            world.Submit(buffer);
        world.Playback();

        var read = world.Changes.Sample(cursor);
        var log = new List<string>();
        foreach (var entry in read.Log)
            log.Add($"{entry.Kind}:{entry.Entity}:{entry.TypeIndex}");
        return $"{world.Get<Position>(moved).X} {world.Has<Velocity>(toggled)} [{string.Join(" ", log)}]";
    }
}
