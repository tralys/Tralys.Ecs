namespace Tralys.Ecs.Tests;

public sealed class RelationPayloadTests
{
    sealed class RecordsChanges : IRelationObserver<Owes>
    {
        public readonly List<(Entity Source, Entity Target, int Old, int New)> Changes = [];

        public void OnRelated(
            in ObserverContext context,
            Entity source,
            Entity target,
            in Owes payload
        ) { }

        public void OnUnrelated(
            in ObserverContext context,
            Entity source,
            Entity target,
            in Owes payload
        ) { }

        public void OnRelationChanged(
            in ObserverContext context,
            Entity source,
            Entity target,
            in Owes oldPayload,
            in Owes newPayload
        ) => Changes.Add((source, target, oldPayload.Amount, newPayload.Amount));
    }

    [Test]
    public async Task Relating_an_existing_edge_with_a_new_payload_logs_and_notifies_the_change()
    {
        using var world = TestWorlds.Create();
        var debtor = world.Create();
        var creditor = world.Create();
        world.Relate(debtor, creditor, new Owes { Amount = 5 });
        var observer = new RecordsChanges();
        world.Observers.OnRelation(observer);
        var cursor = world.Changes.Register("log");

        world.Relate(debtor, creditor, new Owes { Amount = 5 });
        world.Relate(debtor, creditor, new Owes { Amount = 7 });
        world.Playback();

        var logged = new List<(LogKind, Entity, Entity)>();
        foreach (var entry in world.Changes.Sample(cursor).Log)
        {
            if (entry.Is<Owes>())
                logged.Add((entry.Kind, entry.Entity, entry.Other));
        }

        await Assert.That(logged).IsEquivalentTo([(LogKind.RelationChanged, debtor, creditor)]);
        await Assert.That(observer.Changes).IsEquivalentTo([(debtor, creditor, 5, 7)]);
        await Assert
            .That(
                world.TryGetRelation<Owes>(debtor, creditor, out var stored) && stored.Amount == 7
            )
            .IsTrue();
    }
}
