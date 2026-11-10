namespace Tralys.Ecs.Tests;

public sealed class SampleFilterTests
{
    [Test]
    public async Task A_sample_skips_what_queries_skip_unless_its_options_include_it()
    {
        using var world = TestWorlds.Create();
        var dying = world.Create();
        var detached = world.Create();
        var disabled = world.Create();
        world.Set(disabled, new Burning { Ticks = 1 });
        world.EffectRows(dying).Set(new Effect { Kind = 1 });
        world.EffectRows(detached).Set(new Effect { Kind = 1 });
        var strict = world.Changes.Register("strict");
        var withDying = world.Changes.Register(
            "dying",
            new CursorOptions(CategoryMask.All) { IncludePendingDestroy = true }
        );
        var withDetached = world.Changes.Register(
            "detached",
            new CursorOptions(CategoryMask.All) { IncludeDetached = true }
        );

        world.Set(dying, new Position { X = 1 });
        world.Set(dying, new Dialog { Page = 1 });
        using (var edit = world.EffectRows(dying).Edit(1))
            edit.Value.Stacks = 2;
        world.Destroy(dying);
        world.Set(detached, new Position { X = 1 });
        using (var edit = world.EffectRows(detached).Edit(1))
            edit.Value.Stacks = 2;
        world.Detach(detached);
        world.SetEnabled<Burning>(disabled, false);

        var strictSeen = Seen(world, strict);
        var dyingSeen = Seen(world, withDying);
        var detachedSeen = Seen(world, withDetached);

        await Assert.That(strictSeen.Changed).IsEmpty();
        await Assert.That(strictSeen.Logged).IsEquivalentTo([disabled]);
        await Assert.That(dyingSeen.Changed).IsEquivalentTo([dying, dying, dying]);
        await Assert.That(dyingSeen.Logged).IsEquivalentTo([dying, dying, disabled]);
        await Assert.That(detachedSeen.Changed).IsEquivalentTo([detached, detached]);
        await Assert.That(detachedSeen.Logged).IsEquivalentTo([detached, disabled]);
    }

    static (List<Entity> Changed, List<Entity> Logged) Seen(World world, CursorId cursor)
    {
        var sample = world.Changes.Sample(cursor);
        var changed = new List<Entity>();
        foreach (var value in sample.Changed<Position>())
            changed.Add(value.Entity);
        foreach (var value in sample.Changed<Dialog>())
            changed.Add(value.Entity);
        foreach (var value in sample.Changed<Burning>())
            changed.Add(value.Entity);
        foreach (var row in sample.ChangedRows<int, Effect>())
            changed.Add(row.Entity);
        var logged = new List<Entity>();
        foreach (var entry in sample.Log)
        {
            if (entry.TypeIndex >= 0)
                logged.Add(entry.Entity);
        }
        return (changed, logged);
    }

    [Test]
    public async Task An_entity_leaving_pending_destroy_is_logged_with_its_subtree()
    {
        using var world = TestWorlds.Create();
        var cancelled = world.Create();
        var child = world.Create();
        world.SetParent(child, cancelled);
        var doomed = world.Create();
        var moved = world.Create();
        world.SetParent(moved, doomed);
        var played = world.Create();
        world.Destroy(cancelled);
        world.Destroy(doomed);
        world.Destroy(played);
        var cursor = world.Changes.Register("spared");

        world.CancelDestroy(cancelled);
        world.ClearParent(moved);
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.CancelDestroy(played);
        world.Submit(buffer);
        world.Playback();

        var spared = new List<Entity>();
        foreach (var entry in world.Changes.Sample(cursor).Log)
        {
            if (entry.Kind == LogKind.DestroyCancelled)
                spared.Add(entry.Entity);
        }

        await Assert.That(spared).IsEquivalentTo([cancelled, child, moved, played]);
    }
}
