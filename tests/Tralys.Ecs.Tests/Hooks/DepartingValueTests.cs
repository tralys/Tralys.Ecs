namespace Tralys.Ecs.Tests;

public sealed class DepartingValueTests
{
    sealed class Recorder : IRemoveObserver<Health>
    {
        public readonly List<int> Seen = [];

        public void OnRemove(in ObserverContext context, Entity entity, in Health oldValue) =>
            Seen.Add(oldValue.Value);
    }

    [Test]
    public async Task A_remove_observer_hears_the_departing_value_not_the_row_that_fills_its_hole()
    {
        using var world = TestWorlds.Create();
        var recorder = new Recorder();
        world.Observers.OnRemove(recorder);
        var first = world.Create();
        world.Set(first, new Health { Value = 1 });
        var last = world.Create();
        world.Set(last, new Health { Value = 2 });

        world.Remove<Health>(first);
        world.Destroy(last);
        world.Playback();

        await Assert
            .That(recorder.Seen)
            .IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
