namespace Tralys.Ecs.Tests;

public sealed class RowDestroyLogTests
{
    [Test]
    public async Task Destroying_an_entity_logs_each_of_its_rows_removed()
    {
        using var world = TestWorlds.Logged();
        var entity = world.Create();
        foreach (var kind in new[] { 4, 7, 9 })
            world.EffectRows(entity).Set(new Effect { Kind = kind });
        var tail = new LogTail(world);

        world.Destroy(entity);
        world.Playback();

        var removed = 0;
        foreach (var entry in tail.Read())
        {
            if (entry.Kind == LogKind.RowRemoved && entry.Entity == entity && entry.Is<Effect>())
                removed++;
        }
        await Assert.That(removed).IsEqualTo(3);
    }
}
