namespace Tralys.Ecs.Tests;

public sealed partial class AttributionSoakTests
{
    static readonly SystemId Spawner = SystemId.Next();
    static readonly SystemId Wounder = SystemId.Next();
    static readonly SystemId Reaper = SystemId.Next();

    [Query(Parallel = true)]
    static void Wound(Entity entity, ref Health health, Commands commands)
    {
        health.Value--;
        if (health.Value == 2)
            commands.Remove<Velocity>(entity);
    }

    [Query(Parallel = true)]
    static void Reap(Entity entity, in Health health, Commands commands)
    {
        if (health.Value <= 0)
            commands.Destroy(entity);
    }

    [Test]
    public async Task Who_removed_X_names_the_recording_system_after_playback()
    {
        using var world = TestWorlds.Create(new WorldOptions { AttributedLogCapacity = 1 << 15 });
        var lastChild = default(Entity);
        for (var tick = 0; tick < 200; tick++)
        {
            using (world.BeginSystemRun(Spawner))
            {
                var parent = world.Create();
                world.Set(parent, new Health { Value = 3 + (tick % 5) });
                world.Set(parent, new Velocity { X = 1 });
                lastChild = world.Create(new Mover { Position = new Position { X = tick } });
                world.SetParent(lastChild, parent);
            }
            using (world.BeginSystemRun(Wounder))
                RunWound(world);
            world.Playback();
            using (world.BeginSystemRun(Reaper))
                RunReap(world);
            world.Playback();
        }
        for (var tick = 0; tick < 8; tick++)
        {
            using (world.BeginSystemRun(Wounder))
                RunWound(world);
            world.Playback();
            using (world.BeginSystemRun(Reaper))
                RunReap(world);
            world.Playback();
        }

        var log = world.AttributedLog;
        var wrong = new List<string>();
        int destroyed = 0,
            removed = 0,
            added = 0;
        var removeLine = -1;
        var destroyLine = -1;
        for (var i = 0; i < log.Count; i++)
        {
            var entry = log[i];
            switch (entry.Entry.Kind)
            {
                case LogKind.Created:
                    if (entry.Origin != Spawner || entry.Executing != Spawner)
                        wrong.Add($"created by {entry.Origin} in {entry.Executing}");
                    break;
                case LogKind.Added:
                    added++;
                    if (entry.Origin != Spawner || !FromHere(entry))
                        wrong.Add($"added by {entry.Origin} at {entry.ChangedBy}");
                    break;
                case LogKind.Destroyed:
                    destroyed++;
                    destroyLine = entry.ChangedBy.Line;
                    if (entry.Origin != Reaper || entry.Executing != default || !FromHere(entry))
                        wrong.Add(
                            $"destroyed by {entry.Origin} in {entry.Executing} at {entry.ChangedBy}"
                        );
                    break;
                case LogKind.Removed when entry.Entry.Is<Velocity>():
                    removed++;
                    removeLine = entry.ChangedBy.Line;
                    if (entry.Origin != Wounder || entry.Executing != default || !FromHere(entry))
                        wrong.Add(
                            $"removed by {entry.Origin} in {entry.Executing} at {entry.ChangedBy}"
                        );
                    break;
            }
        }

        await Assert.That(wrong).IsEmpty();
        await Assert.That(destroyed).IsEqualTo(400);
        await Assert.That(removed).IsEqualTo(200);
        await Assert.That(added).IsEqualTo(200 * 5);
        await Assert.That(removeLine).IsNotEqualTo(destroyLine);
        await Assert.That(log.TryFindLast(lastChild, LogKind.Destroyed, out var who)).IsTrue();
        await Assert.That(who.Origin).IsEqualTo(Reaper);
    }

    static bool FromHere(in AttributedLogEntry entry) =>
        entry.ChangedBy.File?.EndsWith(nameof(AttributionSoakTests) + ".cs") == true;
}
