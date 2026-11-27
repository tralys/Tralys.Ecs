namespace Tralys.Ecs.Tests;

public sealed class RetargetTests
{
    [Test]
    public async Task A_waiting_retarget_counts_the_edges_recorded_before_it()
    {
        using var world = TestWorlds.Create();
        var source = world.Create();
        var other = world.Create();
        var target = world.Create();
        var next = world.Create();

        var (missing, recorded) = RetargetInWalk(world, source, other, target, next);

        await Assert.That(missing).IsTrue();
        await Assert.That(recorded).IsFalse();
        await Assert.That(world.HasRelation<Follows>(source, next)).IsTrue();
    }

    static (bool Missing, bool Recorded) RetargetInWalk(
        World world,
        Entity source,
        Entity other,
        Entity target,
        Entity next
    )
    {
        using var walk = world.BeginIteration();
        world.Relate<Follows>(source, other);
        var missing = Throws(() => world.Retarget<Follows>(source, target, next));
        world.Relate<Follows>(source, target);
        var recorded = Throws(() => world.Retarget<Follows>(source, target, next));
        return (missing, recorded);
    }

    static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
