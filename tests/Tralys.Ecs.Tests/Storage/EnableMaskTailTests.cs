namespace Tralys.Ecs.Tests;

public sealed class EnableMaskTailTests
{
    [Test]
    public async Task A_vacated_tail_row_leaves_no_enable_bit_behind()
    {
        using var world = TestWorlds.Create();
        var entities = new Entity[3];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create();
            world.Set(entities[i], new Burning { Ticks = i });
        }
        world.Remove<Burning>(entities[2]);
        world.Remove<Burning>(entities[0]);

        var slot = world.allocator.Slots[entities[1].Index];
        var archetype = slot.Archetype!;

        await Assert.That(archetype.Count).IsEqualTo(1);
        await Assert.That(archetype.EnableWords[0] >> archetype.Count).IsEqualTo(0UL);
    }
}
