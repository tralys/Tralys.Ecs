namespace Tralys.Ecs.Tests;

public sealed class BatchPayloadTests
{
    [Test]
    public async Task Entities_built_in_batches_between_idle_playbacks_allocate_nothing()
    {
        using var world = TestWorlds.Create();

        var bytes = AllocationAssert.BytesPerCall(() =>
        {
            var entity = world.Create();
            using (world.Batch(entity))
                world.Set(entity, new Position { X = 1 });
            world.Destroy(entity);
            world.Playback();
        });

        await Assert.That(bytes).IsEqualTo(0);
    }
}
