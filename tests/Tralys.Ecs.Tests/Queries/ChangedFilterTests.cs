namespace Tralys.Ecs.Tests;

public sealed partial class ChangedFilterTests
{
    [Query, Changed<Position>]
    static void Moved(Entity entity, [Arg] List<Entity> seen) => seen.Add(entity);

    [Test]
    public async Task A_write_after_a_changed_run_is_seen_by_the_next_run()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Position());
        RunMoved(world, []);

        world.Set(entity, new Position { X = 5 });
        var seen = new List<Entity>();
        RunMoved(world, seen);

        await Assert.That(seen).IsEquivalentTo([entity]);
    }
}
