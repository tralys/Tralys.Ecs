namespace Tralys.Ecs.Tests;

public sealed class FoldLogTests
{
    [Test]
    public async Task Folding_cancels_only_what_did_not_exist_before_playback()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Velocity { X = 1 });
        var cursor = world.Changes.Register("log");

        using (world.BeginDelivery())
        {
            world.Remove<Velocity>(entity);
            world.Set(entity, new Velocity { X = 5 });
            world.Set(entity, new Position { X = 1 });
            world.Remove<Position>(entity);
        }
        world.Playback();

        var kinds = new List<LogKind>();
        var positionEntries = 0;
        foreach (var entry in world.Changes.Sample(cursor).Log)
        {
            if (entry.Is<Velocity>())
                kinds.Add(entry.Kind);
            if (entry.Is<Position>())
                positionEntries++;
        }

        await Assert
            .That(kinds)
            .IsEquivalentTo(
                [LogKind.Removed, LogKind.Added],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        await Assert.That(positionEntries).IsEqualTo(0);
        await Assert.That(world.Get<Velocity>(entity).X).IsEqualTo(5f);
    }

    sealed class IgnoresAdds : IAddObserver<Burning>
    {
        public void OnAdd(in ObserverContext context, Entity entity, in Burning value) { }
    }

    [Test]
    public async Task Remove_add_remove_of_an_existing_component_folds_to_one_removal()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Burning { Ticks = 1 });
        world.Observers.OnAdd(new IgnoresAdds());
        var cursor = world.Changes.Register("log");

        using (world.BeginDelivery())
        {
            world.Remove<Burning>(entity);
            world.Set(entity, new Burning { Ticks = 2 });
            world.SetEnabled<Burning>(entity, false);
            world.Remove<Burning>(entity);
        }
        world.Playback();

        var kinds = new List<LogKind>();
        foreach (var entry in world.Changes.Sample(cursor).Log)
        {
            if (entry.Is<Burning>())
                kinds.Add(entry.Kind);
        }

        await Assert.That(world.Has<Burning>(entity)).IsFalse();
        await Assert.That(kinds).IsEquivalentTo([LogKind.Removed]);
    }
}
