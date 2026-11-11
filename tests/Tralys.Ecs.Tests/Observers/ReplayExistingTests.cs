namespace Tralys.Ecs.Tests;

public sealed class ReplayExistingTests
{
    sealed class CountsAdds<T> : IAddObserver<T>
    {
        public int Count;

        public void OnAdd(in ObserverContext context, Entity entity, in T value) => Count++;
    }

    [Test]
    public async Task Replay_reaches_only_the_observer_being_registered()
    {
        using var world = TestWorlds.Create();
        var earlier = new CountsAdds<Position>();
        world.Observers.OnAdd(earlier);
        var entity = world.Create();
        world.Set(entity, new Position { X = 1 });
        world.Playback();

        var later = new CountsAdds<Position>();
        world.Observers.OnAdd(later, replayExisting: true);
        world.Playback();

        await Assert.That(earlier.Count).IsEqualTo(1);
        await Assert.That(later.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Replay_covers_sparse_tags()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Add<Rare>(entity);

        var observer = new CountsAdds<Rare>();
        world.Observers.OnAdd(observer, replayExisting: true);
        world.Playback();

        await Assert.That(observer.Count).IsEqualTo(1);
    }
}
