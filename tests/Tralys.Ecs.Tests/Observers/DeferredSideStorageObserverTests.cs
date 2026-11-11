namespace Tralys.Ecs.Tests;

public sealed class DeferredSideStorageObserverTests
{
    sealed class Counter : IRelationObserver<Follows>, IAddObserver<Effect>
    {
        public int Related;
        public int Added;

        public void OnRelated(
            in ObserverContext context,
            Entity source,
            Entity target,
            in Follows payload
        ) => Related++;

        public void OnUnrelated(
            in ObserverContext context,
            Entity source,
            Entity target,
            in Follows payload
        ) { }

        public void OnRelationChanged(
            in ObserverContext context,
            Entity source,
            Entity target,
            in Follows oldPayload,
            in Follows newPayload
        ) { }

        public void OnAdd(in ObserverContext context, Entity entity, in Effect value) => Added++;
    }

    [Test]
    public async Task A_deferred_relation_and_row_edit_each_notify_once_at_playback()
    {
        using var world = TestWorlds.Create();
        var counter = new Counter();
        world.Observers.OnRelation<Follows>(counter);
        world.Observers.OnRowAdd<Effect>(counter);
        var a = world.Create();
        var b = world.Create();

        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.Relate(a, b, new Follows());
        buffer.Commands.EffectRows(a).Set(new Effect { Kind = 3 });
        world.Submit(buffer);
        world.Playback();
        world.Playback();

        await Assert.That((counter.Related, counter.Added)).IsEqualTo((1, 1));
    }
}
