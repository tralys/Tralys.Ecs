namespace Tralys.Ecs.Tests;

public sealed class ObserverMutationTests
{
    sealed class RemovesItself : IAddObserver<Position>
    {
        public ObserverId Id;
        public int Count;

        public void OnAdd(in ObserverContext context, Entity entity, in Position value)
        {
            Count++;
            context.World.Observers.Remove(Id);
        }
    }

    [Test]
    public async Task An_observer_can_remove_itself_during_delivery()
    {
        using var world = TestWorlds.Create();
        var observer = new RemovesItself();
        observer.Id = world.Observers.OnAdd(observer);
        world.Set(world.Create(), new Position { X = 1 });
        world.Set(world.Create(), new Position { X = 2 });

        world.Playback();

        await Assert.That(observer.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Removing_an_observer_that_is_no_longer_registered_throws()
    {
        using var world = TestWorlds.Create();
        var id = world.Observers.OnAdd(new RemovesItself());
        world.Observers.Remove(id);

        await Assert.That(() => world.Observers.Remove(id)).Throws<ArgumentException>();
    }
}
