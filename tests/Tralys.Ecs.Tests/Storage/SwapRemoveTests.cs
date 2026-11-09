namespace Tralys.Ecs.Tests;

public sealed class SwapRemoveTests
{
    [Test]
    public async Task The_entity_moved_into_a_vacated_row_keeps_reading_its_own_values()
    {
        using var world = TestWorlds.Create();
        var first = world.Create();
        world.Set(first, new Health { Value = 1 });
        var middle = world.Create();
        world.Set(middle, new Health { Value = 2 });
        var last = world.Create();
        world.Set(last, new Health { Value = 3 });

        world.Remove<Health>(first);

        await Assert.That(world.Get<Health>(last).Value).IsEqualTo(3);
        await Assert.That(world.Get<Health>(middle).Value).IsEqualTo(2);
    }
}
