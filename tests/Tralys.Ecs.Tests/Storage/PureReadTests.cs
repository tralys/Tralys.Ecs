namespace Tralys.Ecs.Tests;

public sealed class PureReadTests
{
    [Test]
    public async Task Reading_never_written_sparse_relation_and_row_types_allocates_nothing()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        Read(world, entity);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            Read(world, entity);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0L);
        await Assert.That(world.sparse.All(s => s is null)).IsTrue();
        await Assert.That(world.sparseTags.All(s => s is null)).IsTrue();
        await Assert.That(world.relations.All(s => s is null)).IsTrue();
        await Assert.That(world.rowStores.All(s => s is null)).IsTrue();
    }

    static void Read(World world, Entity entity)
    {
        world.Has<Dialog>(entity);
        world.TryGet<Dialog>(entity, out _);
        world.Has<Rare>(entity);
        world.Targets<Follows>(entity);
        world.Sources<Follows>(entity);
        world.HasRelation<Follows>(entity, entity);
        world.Children(entity);
        world.Parent(entity);
        world.EffectRows(entity).TryGet(1, out _);
        foreach (ref readonly var row in world.EffectRows(entity))
            _ = row.Stacks;
    }
}
