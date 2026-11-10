namespace Tralys.Ecs.Tests;

public sealed class CursorReuseTests
{
    [Test]
    public async Task An_unregistered_cursor_id_does_not_address_the_cursor_that_reused_its_slot()
    {
        using var world = TestWorlds.Create();
        var stale = world.Changes.Register("first");
        world.Changes.Unregister(stale);
        var reused = world.Changes.Register("second");

        await Assert.That(() => world.Changes.Unregister(stale)).Throws<ArgumentException>();
        await Assert.That(world.Changes.NameOf(reused)).IsEqualTo("second");
    }
}
