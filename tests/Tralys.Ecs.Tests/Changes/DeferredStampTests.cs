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
    public async Task An_untracked_sparse_component_never_reads_as_changed()
    {
        using var world = TestWorlds.Create();
        var cursor = world.Changes.Register("untracked");
        var entity = world.Create();
        world.AdvanceVersion();

        world.Set(entity, new Footnote { Line = 1 });
        using (var edit = world.Edit<Footnote>(entity))
            edit.Value.Line = 2;

        var changed = 0;
        foreach (var _ in world.Changes.Sample(cursor).Changed<Footnote>())
            changed++;
        await Assert.That(changed).IsEqualTo(0);
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
