namespace Tralys.Ecs.Tests;

public sealed class ReservationReleaseTests
{
    [Test]
    public async Task A_reservation_destroyed_before_playback_frees_its_slot()
    {
        using var world = TestWorlds.Create();
        Entity reserved;
        using (world.BeginDelivery())
        {
            reserved = world.Create();
            world.Destroy(reserved);
        }
        world.Playback();
        var next = world.Create();

        await Assert.That(world.StateOf(reserved)).IsEqualTo(LifecycleState.Dead);
        await Assert.That(next.Index).IsEqualTo(reserved.Index);
    }
}
