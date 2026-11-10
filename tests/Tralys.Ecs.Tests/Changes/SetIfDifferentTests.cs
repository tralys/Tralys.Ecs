namespace Tralys.Ecs.Tests;

public sealed class SetIfDifferentTests
{
    [Test]
    public async Task Writing_an_equal_value_leaves_the_change_stamp_alone()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Health { Value = 5 });
        var stamped = world.Changes.VersionOf<Health>(entity);
        world.AdvanceVersion();

        world.Set(entity, new Health { Value = 5 });
        var afterEqualSet = world.Changes.VersionOf<Health>(entity);
        using (var edit = world.Edit<Health>(entity))
            edit.Value.Value = 5;
        var afterEqualEdit = world.Changes.VersionOf<Health>(entity);
        using (var edit = world.Edit<Health>(entity))
            edit.Value.Value = 6;
        var afterRealEdit = world.Changes.VersionOf<Health>(entity);

        await Assert.That(afterEqualSet).IsEqualTo(stamped);
        await Assert.That(afterEqualEdit).IsEqualTo(stamped);
        await Assert.That(afterRealEdit).IsGreaterThan(stamped);
    }
}
