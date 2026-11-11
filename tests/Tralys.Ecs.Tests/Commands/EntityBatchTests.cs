namespace Tralys.Ecs.Tests;

public sealed class EntityBatchTests
{
    [Test]
    public async Task A_batch_of_three_adds_moves_the_entity_once()
    {
        using var world = TestWorlds.Create();
        var single = world.Create();
        var entity = world.Create();
        var before = world.structuralCount;
        world.Set(single, new Health { Value = 1 });
        var oneMove = world.structuralCount - before;

        before = world.structuralCount;
        using (var batch = world.Batch(entity))
        {
            batch.Set(new Position { X = 1 });
            batch.Set(new Velocity { X = 2 });
            batch.Add<Npc>();
        }

        await Assert.That(world.structuralCount - before).IsEqualTo(oneMove);
        await Assert
            .That(
                world.Has<Position>(entity) && world.Has<Velocity>(entity) && world.Has<Npc>(entity)
            )
            .IsTrue();
    }

    [Test]
    public async Task A_batch_creates_only_the_archetype_it_lands_in()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        var before = world.Introspection.ArchetypeCount;

        using (var batch = world.Batch(entity))
        {
            batch.Set(new Position { X = 1 });
            batch.Set(new Velocity { X = 2 });
            batch.Add<Npc>();
            world.Detach(entity);
        }

        await Assert.That(world.Introspection.ArchetypeCount).IsEqualTo(before + 1);
        await Assert.That(world.IsDetached(entity) && world.Has<Npc>(entity)).IsTrue();
    }
}
