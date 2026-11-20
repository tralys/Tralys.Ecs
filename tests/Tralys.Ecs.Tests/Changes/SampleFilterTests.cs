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
            new CursorOptions { IncludePendingDestroy = true }
        );
        var withDetached = world.Changes.Register(
            "detached",
            new CursorOptions { IncludeDetached = true }
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

        await Assert.That(Seen(world, strict)).IsEmpty();
        await Assert.That(Seen(world, withDying)).IsEquivalentTo([dying, dying, dying]);
        await Assert.That(Seen(world, withDetached)).IsEquivalentTo([detached, detached]);
    }

    static List<Entity> Seen(World world, CursorId cursor)
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
        return changed;
    }

    [Test]
    public async Task An_entity_leaving_pending_destroy_is_logged_with_its_subtree()
    {
        using var world = TestWorlds.Logged();
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
        var tail = new LogTail(world);

        world.CancelDestroy(cancelled);
        world.ClearParent(moved);
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.CancelDestroy(played);
        world.Submit(buffer);
        world.Playback();

        var spared = new List<Entity>();
        foreach (var entry in tail.Read())
        {
            if (entry.Kind == LogKind.DestroyCancelled)
                spared.Add(entry.Entity);
        }

        await Assert.That(spared).IsEquivalentTo([cancelled, child, moved, played]);
    }
}
