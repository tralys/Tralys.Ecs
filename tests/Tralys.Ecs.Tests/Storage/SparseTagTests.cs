namespace Tralys.Ecs.Tests;

public sealed class SparseTagTests
{
    [Test]
    public async Task RemoveAll_clears_a_sparse_tag()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Add<Rare>(entity);

        world.RemoveAll<Rare>();

        await Assert.That(world.Has<Rare>(entity)).IsFalse();
    }
}
