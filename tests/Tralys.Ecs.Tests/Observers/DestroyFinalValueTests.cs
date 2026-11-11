namespace Tralys.Ecs.Tests;

public sealed class DestroyFinalValueTests
{
    sealed class Reader : IDestroyObserver
    {
        public int Seen = -1;

        public void OnDestroy(in ObserverContext context, Entity entity) =>
            Seen = context.World.Get<Health>(entity).Value;
    }

    [Test]
    public async Task OnDestroy_reads_the_victim_s_components()
    {
        using var world = TestWorlds.Create();
        var reader = new Reader();
        world.Observers.OnDestroy(reader);
        var victim = world.Create();
        world.Set(victim, new Health { Value = 42 });

        world.Destroy(victim);
        world.Playback();

        await Assert.That(reader.Seen).IsEqualTo(42);
        await Assert.That(world.Exists(victim)).IsFalse();
    }
}
