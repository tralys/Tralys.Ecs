namespace Tralys.Ecs.Tests;

public sealed class RowDestroyLogTests
{
    [Test]
    public async Task Destroying_an_entity_logs_each_of_its_rows_removed()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        foreach (var kind in new[] { 4, 7, 9 })
            world.EffectRows(entity).Set(new Effect { Kind = kind });
        var cursor = world.Changes.Register("rows");

        world.Destroy(entity);
        world.Playback();

        var read = world.Changes.Sample(cursor);
        var removed = new List<int>();
        foreach (var entry in read.Log)
        {
            if (entry.Kind == LogKind.RowRemoved)
                removed.Add(read.RowKey<int, Effect>(entry));
        }
        await Assert.That(removed).IsEquivalentTo([4, 7, 9]);
    }
}
