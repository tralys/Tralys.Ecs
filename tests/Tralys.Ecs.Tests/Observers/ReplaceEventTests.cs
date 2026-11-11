namespace Tralys.Ecs.Tests;

public sealed class ReplaceEventTests
{
    sealed class CountsReplaces : IReplaceObserver<Medal>
    {
        public int Count;

        public void OnReplace(
            in ObserverContext context,
            Entity entity,
            in Medal oldValue,
            in Medal newValue
        ) => Count++;
    }

    [Test]
    public async Task A_buffered_sparse_replacement_notifies_replace_observers()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Medal { Rank = 1 });
        world.Playback();
        var observer = new CountsReplaces();
        world.Observers.OnReplace(observer);

        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Set(entity, new Medal { Rank = 2 });
        world.Submit(buffer);
        world.Playback();

        await Assert.That(observer.Count).IsEqualTo(1);
    }
}
