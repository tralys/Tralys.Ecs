namespace Tralys.Ecs.Tests;

public sealed class DeferredStampTests
{
    [Test]
    public async Task A_deferred_remove_and_re_add_stamps_the_row_as_added()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Position { X = 1 });
        var cursor = world.Changes.Register("readd");

        using (world.BeginDelivery())
        {
            world.Remove<Position>(entity);
            world.Set(entity, new Position { X = 1 });
        }
        world.Playback();

        var added = 0;
        foreach (var row in world.Changes.Sample(cursor).Changed<Position>())
        {
            if (row.Added)
                added++;
        }
        await Assert.That(added).IsEqualTo(1);
    }

    [Test]
    public async Task A_deferred_add_issued_untracked_stays_unstamped()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();

        using (world.BeginDelivery())
        using (world.Untracked())
            world.Set(entity, new Position { X = 1 });
        world.Playback();

        await Assert.That(world.Changes.VersionOf<Position>(entity)).IsEqualTo(0UL);
    }

    [Test]
    public async Task A_type_not_marked_tracked_carries_no_stamps()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Velocity { X = 1 });
        world.Set(entity, new Footnote { Line = 1 });
        world.TallyRows(entity).Set(new Tally { Slot = 1, Count = 1 });

        var archetype = world.allocator.Slots[entity.Index].Archetype!;
        var sparse = world.SparseFor(world.registry.IndexOf<Footnote>());
        var rows = world.FindRows<int, Tally>()!.BlockOf(entity)!;

        await Assert.That(archetype.TrackedSlot(world.registry.IndexOf<Velocity>())).IsEqualTo(-1);
        await Assert.That(sparse.Changed.Length + sparse.Added.Length).IsEqualTo(0);
        await Assert.That(rows.Changed.Length + rows.Added.Length).IsEqualTo(0);
    }

    [Test]
    public async Task An_enableable_tag_reports_its_stamps()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Add<Frozen>(entity);
        var added = world.Changes.AddedVersionOf<Frozen>(entity);
        world.AdvanceVersion();

        world.SetEnabled<Frozen>(entity, false);
        var disabled = world.Changes.VersionOf<Frozen>(entity);
        world.SetEnabled<Frozen>(entity, true);

        await Assert.That(added).IsGreaterThan(0UL);
        await Assert.That(disabled).IsEqualTo(0UL);
        await Assert.That(world.Changes.VersionOf<Frozen>(entity)).IsGreaterThan(added);
    }
}
