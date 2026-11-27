namespace Tralys.Ecs.Tests;

public sealed class UniqueOrderTests
{
    [Test]
    public async Task A_parallel_set_takes_its_key_at_playback_even_beside_an_older_serial_record()
    {
        using var world = TestWorlds.Create();
        var returning = world.Create();
        world.Detach(returning);
        var rival = world.Create();
        var buffer = world.RentBuffer(-1, 0);
        buffer.Commands.Set(returning, new Badge { Number = 1 });
        world.Submit(buffer);
        RecordReattach(world, returning);
        world.Set(rival, new Badge { Number = 1 });

        world.Playback();

        await Assert.That(world.BadgeByNumber().Get(1)).IsEqualTo(returning);
    }

    static void RecordReattach(World world, Entity entity)
    {
        using var delivery = world.BeginDelivery();
        world.Reattach(entity);
    }
}
