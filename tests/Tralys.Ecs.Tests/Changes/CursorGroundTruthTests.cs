namespace Tralys.Ecs.Tests;

public sealed class CursorGroundTruthTests
{
    [Test]
    public async Task Every_cursor_sees_exactly_the_value_changes_since_its_last_commit()
    {
        var failures = Run(seed: 1, 100_000);

        await Assert.That(failures).IsEmpty();
    }

    static List<string> Run(int seed, int operations)
    {
        using var world = TestWorlds.Create();
        var random = new Random(seed);
        var fast = world.Changes.Register("fast");
        var slow = world.Changes.Register("slow");
        var expectedFast = new HashSet<Entity>();
        var expectedSlow = new HashSet<Entity>();
        var live = new List<Entity>();
        var failures = new List<string>();

        void Changed(Entity entity)
        {
            expectedFast.Add(entity);
            expectedSlow.Add(entity);
        }

        void Gone(Entity entity)
        {
            expectedFast.Remove(entity);
            expectedSlow.Remove(entity);
        }

        float Pick() => random.Next(4);

        for (var op = 0; op < operations; op++)
        {
            if (op % 50 == 0)
                world.AdvanceVersion();

            var entity = live.Count > 0 ? live[random.Next(live.Count)] : default;
            switch (random.Next(9))
            {
                case 0:
                {
                    var created = world.Create();
                    world.Set(created, new Position { X = Pick() });
                    live.Add(created);
                    Changed(created);
                    break;
                }
                case 1 when live.Count > 0 && world.Has<Position>(entity):
                {
                    var next = new Position { X = Pick() };
                    if (world.Get<Position>(entity).X != next.X)
                        Changed(entity);
                    world.Set(entity, next);
                    break;
                }
                case 2 when live.Count > 0 && world.Has<Position>(entity):
                {
                    using var edit = world.Edit<Position>(entity);
                    var next = Pick();
                    if (edit.Value.X != next)
                        Changed(entity);
                    edit.Value.X = next;
                    break;
                }
                case 3 when live.Count > 0 && world.Has<Position>(entity):
                    world.Remove<Position>(entity);
                    Gone(entity);
                    break;
                case 4 when live.Count > 0 && !world.Has<Position>(entity):
                    world.Set(entity, new Position { X = Pick() });
                    Changed(entity);
                    break;
                case 5 when live.Count > 0:
                    world.Destroy(entity);
                    live.Remove(entity);
                    Gone(entity);
                    break;
                case 6 when live.Count > 0:
                    if (world.Has<Velocity>(entity))
                        world.Remove<Velocity>(entity);
                    else
                        world.Set(entity, new Velocity { X = 1 });
                    break;
                case 7:
                    world.Playback();
                    break;
            }

            if (op % 7 == 3)
                Sample(world, fast, expectedFast, op, failures);
            if (op % 13 == 5)
                Sample(world, slow, expectedSlow, op, failures);
            if (failures.Count > 0)
                break;
        }
        return failures;
    }

    static void Sample(
        World world,
        CursorId cursor,
        HashSet<Entity> expected,
        int op,
        List<string> failures
    )
    {
        var read = world.Changes.Sample(cursor);
        var seen = new HashSet<Entity>();
        foreach (var row in read.Changed<Position>())
            seen.Add(row.Entity);
        if (!seen.SetEquals(expected))
            failures.Add(
                $"op {op}, cursor {world.Changes.NameOf(cursor)}: saw {seen.Count}, expected {expected.Count}; "
                    + $"missing {string.Join(",", expected.Except(seen))}, extra {string.Join(",", seen.Except(expected))}"
            );
        world.Changes.Commit(cursor, read.Window);
        expected.Clear();
    }
}
