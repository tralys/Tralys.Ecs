namespace Tralys.Ecs.Tests;

public sealed class ClosedGenericTests
{
    [Test]
    public async Task Two_instantiations_of_one_generic_component_are_two_types()
    {
        var ofInt = TestWorlds.Registry.Get<Sent<int>>();
        var ofPosition = TestWorlds.Registry.Get<Sent<Position>>();
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Sent<int> { Payload = 4, Count = 1 });
        world.Set(
            entity,
            new Sent<Position>
            {
                Payload = new Position { X = 2 },
                Count = 2,
            }
        );

        await Assert.That(ofInt.Index).IsNotEqualTo(ofPosition.Index);
        await Assert.That(world.Get<Sent<int>>(entity).Count).IsEqualTo(1);
        await Assert.That(world.Get<Sent<Position>>(entity).Count).IsEqualTo(2);
    }
}
