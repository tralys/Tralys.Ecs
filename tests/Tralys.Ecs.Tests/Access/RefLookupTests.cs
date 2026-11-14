namespace Tralys.Ecs.Tests;

public sealed class RefLookupTests
{
    [Test]
    public async Task Replacing_through_a_lookup_never_adds_the_component()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        var identities = new RefLookup<Identity>(world);

        await Assert
            .That(() => identities.Replace(entity, new Identity { Id = 3 }))
            .Throws<InvalidOperationException>();
        await Assert.That(world.Has<Identity>(entity)).IsFalse();
    }
}
