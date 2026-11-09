namespace Tralys.Ecs.Tests;

public sealed class DetachedPartitionTests
{
    [Test]
    public async Task A_detached_entity_lives_apart_from_attached_ones_with_the_same_components()
    {
        using var world = TestWorlds.Create();
        var attached = world.Create();
        world.Set(attached, new Health { Value = 1 });
        var offline = world.Create();
        world.Set(offline, new Health { Value = 2 });

        world.Detach(offline);

        var attachedArchetype = world.allocator.Slots[attached.Index].Archetype!;
        var offlineArchetype = world.allocator.Slots[offline.Index].Archetype!;
        await Assert
            .That((world.StateOf(offline), world.IsDetached(offline)))
            .IsEqualTo((LifecycleState.Alive, true));
        await Assert.That(world.IsLive(offline)).IsTrue();
        await Assert.That(offlineArchetype).IsNotEqualTo(attachedArchetype);
        await Assert.That(offlineArchetype.Detached).IsTrue();
        await Assert.That(world.Get<Health>(offline).Value).IsEqualTo(2);
    }

    [Test]
    public async Task Detaching_takes_the_subtree_and_reattaching_keeps_what_detached_itself()
    {
        using var world = TestWorlds.Create();
        var player = world.Create();
        var bag = world.Create();
        var hidden = world.Create();
        world.SetParent(bag, player);
        world.SetParent(hidden, bag);
        world.Detach(hidden);

        world.Detach(player);
        var whileOffline = (world.IsDetached(bag), world.IsDetached(hidden));
        world.Reattach(player);
        var newcomer = world.Create();
        world.SetParent(newcomer, hidden);

        await Assert.That(whileOffline).IsEqualTo((true, true));
        await Assert
            .That((world.IsDetached(player), world.IsDetached(bag), world.IsDetached(hidden)))
            .IsEqualTo((false, false, true));
        await Assert.That(world.IsDetached(newcomer)).IsTrue();
    }

    [Test]
    public async Task A_detach_played_back_moves_the_subtree_after_logging_its_root()
    {
        using var world = TestWorlds.Create();
        var player = world.Create();
        var bag = world.Create();
        world.SetParent(bag, player);
        var cursor = world.Changes.Register("partitions");

        using (world.BeginDelivery())
            world.Detach(player);
        world.Playback();

        var detached = new List<Entity>();
        foreach (var entry in world.Changes.Sample(cursor).Log)
        {
            if (entry.Kind == LogKind.Detached)
                detached.Add(entry.Entity);
        }
        await Assert.That(detached).IsEquivalentTo([player, bag]);
        await Assert.That(detached[0]).IsEqualTo(player);
    }

    [Test]
    public async Task A_capture_takes_detached_children_but_not_detached_entities_it_only_references()
    {
        using var source = TestWorlds.Create();
        var player = source.Create();
        var bag = source.Create();
        source.SetParent(bag, player);
        var friend = source.Create();
        source.Set(player, new Link { Target = friend });
        source.Detach(friend);
        source.Detach(player);

        var image = source.Capture([player]);
        using var target = TestWorlds.Create();
        var restored = target.Restore(image);
        var restoredPlayer = restored.Map(player);
        var restoredBag = restored.Map(bag);
        target.Reattach(restoredPlayer);
        var bagAfterReattach = target.IsDetached(restoredBag);

        await Assert.That(image.EntityCount).IsEqualTo(2);
        await Assert.That(bagAfterReattach).IsFalse();
    }
}
