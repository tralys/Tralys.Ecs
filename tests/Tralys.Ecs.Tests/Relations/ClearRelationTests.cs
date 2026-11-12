namespace Tralys.Ecs.Tests;

public sealed class ClearRelationTests
{
    [Test]
    public async Task ClearSources_removes_the_edges_into_the_target_and_keeps_its_own()
    {
        using var world = TestWorlds.Create();
        var target = world.Create();
        var first = world.Create();
        var second = world.Create();
        var followed = world.Create();
        world.Relate<Follows>(first, target);
        world.Relate<Follows>(second, target);
        world.Relate<Follows>(target, followed);

        world.ClearSources<Follows>(target);

        await Assert
            .That(
                (
                    world.HasRelation<Follows>(first, target),
                    world.HasRelation<Follows>(second, target),
                    world.HasRelation<Follows>(target, followed)
                )
            )
            .IsEqualTo((false, false, true));
    }
}
