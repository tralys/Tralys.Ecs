namespace Tralys.Ecs.Tests;

public sealed class PendingDestroyTests
{
    [Test]
    public async Task A_destroyed_entity_stays_readable_until_the_finalisation_point()
    {
        using var world = TestWorlds.Create();
        var victim = world.Create();
        world.Set(victim, new Health { Value = 9 });

        world.Destroy(victim);
        var afterDestroy = world.Get<Health>(victim).Value;
        var pending = (world.Exists(victim), world.IsLive(victim));
        world.Playback(finalizeDestroys: false);
        var afterOrdinaryPlayback = (world.StateOf(victim), world.Get<Health>(victim).Value);
        world.Playback();

        await Assert.That(afterDestroy).IsEqualTo(9);
        await Assert.That(pending).IsEqualTo((true, false));
        await Assert.That(afterOrdinaryPlayback).IsEqualTo((LifecycleState.PendingDestroy, 9));
        await Assert.That(world.Exists(victim)).IsFalse();
    }
}
