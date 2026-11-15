namespace Tralys.Ecs.Tests;

public sealed class RestoreIndexTests
{
    [Test]
    public async Task A_restored_entity_is_found_by_its_unique_key()
    {
        using var source = TestWorlds.Create();
        var badge = source.Create();
        source.Set(badge, new Badge { Number = 7 });
        var image = source.Capture([badge]);

        using var target = TestWorlds.Create();
        target.Create();
        var restored = target.Restore(image).Map(badge);
        var held = target.BadgeByNumber().TryGet(7, out var found);

        await Assert.That(held).IsTrue();
        await Assert.That(found).IsEqualTo(restored);
    }
}
