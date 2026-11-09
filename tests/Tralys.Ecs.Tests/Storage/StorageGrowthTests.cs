namespace Tralys.Ecs.Tests;

public sealed class StorageGrowthTests
{
    [Test]
    public async Task Each_enableable_type_keeps_its_bits_as_the_archetype_grows()
    {
        using var world = TestWorlds.Create();
        var entities = new Entity[130];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create();
            world.Set(entities[i], new Burning { Ticks = i });
            world.Add<Frozen>(entities[i]);
            world.SetEnabled<Burning>(entities[i], i % 3 != 0);
            world.SetEnabled<Frozen>(entities[i], i % 5 != 0);
        }

        var wrong = Enumerable
            .Range(0, entities.Length)
            .Where(i =>
                world.Has<Burning>(entities[i]) != (i % 3 != 0)
                || world.Has<Frozen>(entities[i]) != (i % 5 != 0)
            )
            .ToList();

        await Assert.That(wrong).IsEmpty();
    }
}
