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

    [Test]
    public async Task Playback_inside_an_open_batch_throws_and_leaves_the_batch_to_its_dispose()
    {
        using var world = TestWorlds.Create();
        var single = world.Create();
        var entity = world.Create();
        var before = world.structuralCount;
        world.Set(single, new Health { Value = 1 });
        var oneMove = world.structuralCount - before;
        before = world.structuralCount;
        Exception? thrown = null;

        using (var batch = world.Batch(entity))
        {
            batch.Set(new Position { X = 1 });
            try
            {
                world.Playback();
            }
            catch (InvalidOperationException error)
            {
                thrown = error;
            }
            batch.Set(new Velocity { X = 2 });
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(world.structuralCount - before).IsEqualTo(oneMove);
        await Assert.That(world.Has<Position>(entity) && world.Has<Velocity>(entity)).IsTrue();
    }

    [Test]
    public async Task A_reset_recovers_a_world_whose_batch_an_exception_left_open()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        var baseline = world.Snapshot();
        try
        {
            AbandonBatch(world, entity);
        }
        catch (InvalidOperationException) { }

        world.Reset(baseline);
        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.Set(entity, new Health { Value = 1 });
        world.Submit(buffer);
        world.Playback();

        await Assert.That(world.Has<Health>(entity)).IsTrue();
    }

    static void AbandonBatch(World world, Entity entity)
    {
        var batch = world.Batch(entity);
        batch.Set(new Position { X = 1 });
        Fail();
        batch.Dispose();
    }

    static void Fail() => throw new InvalidOperationException("the body failed");
}
