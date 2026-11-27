namespace Tralys.Ecs.Tests;

public sealed class ReparentTests
{
    [Test]
    public async Task A_recorded_reparent_that_closes_a_cycle_with_another_recorded_one_throws_at_the_call()
    {
        using var world = TestWorlds.Create();
        var a = world.Create();
        var b = world.Create();

        var threw = SecondCallThrows(world, a, b);

        await Assert.That(threw).IsTrue();
        await Assert.That(world.Parent(a)).IsEqualTo(b);
        await Assert.That(world.Parent(b).IsDefault).IsTrue();
    }

    static bool SecondCallThrows(World world, Entity a, Entity b)
    {
        using var walk = world.BeginIteration();
        world.SetParent(a, b);
        try
        {
            world.SetParent(b, a);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        return false;
    }
}
