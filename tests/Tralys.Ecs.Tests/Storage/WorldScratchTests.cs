namespace Tralys.Ecs.Tests;

public sealed class WorldScratchTests
{
    [Test]
    public async Task Rentals_returned_out_of_order_settle_without_allocating()
    {
        using var world = TestWorlds.Create();

        var bytes = AllocationAssert.BytesPerCall(() =>
        {
            var large = world.Scratch.Rent<int>(64);
            var small = world.Scratch.Rent<int>(4);
            large.Dispose();
            small.Dispose();
        });

        await Assert.That(bytes).IsEqualTo(0);
    }
}
