namespace Tralys.Ecs.Tests;

public sealed partial class UpTermTests
{
    [Query]
    static void Watch(in Position position, Up<Burning> fire, [Arg] List<int> seen) =>
        seen.Add(fire.HasValue ? fire.Value.Ticks : -1);

    [Test]
    public async Task A_row_sees_an_ancestor_disabled_in_an_open_batch_as_absent()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        world.Set(parent, new Burning { Ticks = 3 });
        var child = world.Create();
        world.Set(child, new Position());
        world.SetParent(child, parent);

        var seen = new List<int>();
        RunWatch(world, seen);
        using (world.Batch(parent))
        {
            world.SetEnabled<Burning>(parent, false);
            RunWatch(world, seen);
        }

        await Assert.That(seen).IsEquivalentTo([3, -1]);
    }

    [Query]
    static void Ignite(
        Entity entity,
        in Position position,
        Up<Burning> fire,
        World world,
        [Arg] List<int> seen
    )
    {
        seen.Add(fire.HasValue ? fire.Value.Ticks : -1);
        if (!fire.HasValue)
            world.Set(world.Parent(entity), new Burning { Ticks = 7 });
    }

    [Test]
    public async Task A_later_sibling_sees_an_ancestor_component_recorded_earlier_in_the_walk()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        for (var i = 0; i < 2; i++)
        {
            var child = world.Create();
            world.Set(child, new Position());
            world.SetParent(child, parent);
        }

        var seen = new List<int>();
        RunIgnite(world, seen);

        await Assert.That(seen).IsEquivalentTo([-1, 7]);
    }
}
