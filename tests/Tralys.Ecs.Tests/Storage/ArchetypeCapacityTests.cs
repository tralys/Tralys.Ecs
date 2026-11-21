namespace Tralys.Ecs.Tests;

public sealed class ArchetypeCapacityTests
{
    [Test]
    public async Task An_archetype_left_empty_between_visits_keeps_its_storage()
    {
        using var world = TestWorlds.Create();

        var bytes = AllocationAssert.BytesPerCall(() =>
        {
            var entity = world.Create();
            world.Set(entity, new Position { X = 1 });
            world.Destroy(entity);
            for (var i = 0; i < 100; i++)
                world.Playback();
        });

        await Assert.That(bytes).IsEqualTo(0);
    }

    [Test]
    public async Task Trimming_releases_an_empty_archetypes_storage()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Position { X = 1 });
        var archetype = await Assert
            .That(world.allocator.Slots[entity.Index].Archetype)
            .IsNotNull();
        world.Destroy(entity);
        world.Playback();

        world.TrimExcess();

        await Assert.That(archetype.Capacity).IsEqualTo(0);
    }
}
