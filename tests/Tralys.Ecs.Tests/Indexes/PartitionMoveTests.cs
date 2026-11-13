namespace Tralys.Ecs.Tests;

public sealed class PartitionMoveTests
{
    [Test]
    public async Task A_new_partition_key_takes_the_entity_out_of_its_old_partition()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Placement { Map = 1 });

        world.Set(entity, new Placement { Map = 2 });
        var left = world.PlacementByMap().Count(1);
        var joined = world.PlacementByMap().Get(2).Contains(entity);

        await Assert.That(left).IsEqualTo(0);
        await Assert.That(joined).IsTrue();
    }
}
