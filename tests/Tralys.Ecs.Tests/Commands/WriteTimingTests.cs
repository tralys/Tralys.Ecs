namespace Tralys.Ecs.Tests;

public sealed class WriteTimingTests
{
    static List<string> Logged(LogTail tail)
    {
        var kinds = new List<string>();
        foreach (var entry in tail.Read())
        {
            if (entry.TypeIndex >= 0)
                kinds.Add(entry.Kind.ToString());
        }
        return kinds;
    }

    [Test]
    public async Task Inside_an_observer_every_presence_change_waits_for_playback_and_reads_see_it_at_once()
    {
        using var world = TestWorlds.Logged();
        var holder = world.Create();
        world.Set(holder, new Burning { Ticks = 1 });
        var tail = new LogTail(world);

        bool seen;
        using (world.BeginDelivery())
        {
            world.Set(holder, new Dialog { Page = 2 });
            world.SetEnabled<Burning>(holder, false);
            seen = world.Has<Dialog>(holder) && !world.Has<Burning>(holder);
        }
        var beforePlayback = Logged(tail);
        world.Playback();
        var afterPlayback = Logged(tail);

        await Assert.That(seen).IsTrue();
        await Assert.That(beforePlayback).IsEmpty();
        await Assert.That(afterPlayback).IsEquivalentTo(["Disabled", "Added"]);
    }

    [Test]
    public async Task A_flip_undone_inside_an_observer_reads_the_stored_value()
    {
        using var world = TestWorlds.Create();
        var holder = world.Create();
        world.Set(holder, new Burning { Ticks = 7 });

        Burning read;
        using (world.BeginDelivery())
        {
            world.SetEnabled<Burning>(holder, false);
            world.SetEnabled<Burning>(holder, true);
            world.TryGet(holder, out read);
        }
        world.Playback();

        await Assert.That(read.Ticks).IsEqualTo(7);
    }

    [Test]
    public async Task A_bundle_created_and_destroyed_inside_an_observer_logs_nothing()
    {
        using var world = TestWorlds.Logged();
        var tail = new LogTail(world);

        using (world.BeginDelivery())
        {
            var created = world.Create(new RareMover { Position = new Position { X = 1 } });
            world.Destroy(created);
        }
        world.Playback();

        await Assert.That(Logged(tail)).IsEmpty();
    }

    [Test]
    public async Task Removing_every_holder_inside_an_observer_includes_a_pending_sparse_add()
    {
        using var world = TestWorlds.Create();
        var holder = world.Create();

        using (world.BeginDelivery())
        {
            world.Set(holder, new Dialog { Page = 1 });
            world.RemoveAll<Dialog>();
        }
        world.Playback();

        await Assert.That(world.Has<Dialog>(holder)).IsFalse();
    }

    static int Remove(
        World world,
        Entity entity,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0
    )
    {
        world.Remove<Health>(entity, file: file, line: line);
        return line;
    }

    static int Set(
        World world,
        Entity entity,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0
    )
    {
        world.Set(entity, new Health { Value = 2 }, file: file, line: line);
        return line;
    }

    [Test]
    public async Task The_log_credits_a_re_add_s_removal_and_add_to_their_own_writes()
    {
        using var world = TestWorlds.Create(new WorldOptions { AttributedLogCapacity = 64 });
        var holder = world.Create();
        world.Set(holder, new Health { Value = 1 });
        var start = world.AttributedLog.Count;

        int removeLine,
            addLine;
        using (world.BeginDelivery())
        {
            removeLine = Remove(world, holder);
            addLine = Set(world, holder);
        }
        world.Playback();

        var credited = new List<(LogKind, int)>();
        for (var i = start; i < world.AttributedLog.Count; i++)
        {
            var entry = world.AttributedLog[i];
            if (entry.Entry.Is<Health>())
                credited.Add((entry.Entry.Kind, entry.ChangedBy.Line));
        }
        await Assert
            .That(credited)
            .IsEquivalentTo([(LogKind.Removed, removeLine), (LogKind.Added, addLine)]);
    }

    static int Enable(
        World world,
        Entity entity,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0
    )
    {
        world.SetEnabled<Burning>(entity, true, file: file, line: line);
        return line;
    }

    [Test]
    public async Task A_recorded_set_of_the_held_value_takes_no_credit()
    {
        using var world = TestWorlds.Create(new WorldOptions { AttributedLogCapacity = 64 });
        var holder = world.Create();
        world.Set(holder, new Burning { Ticks = 1 });
        world.SetEnabled<Burning>(holder, false);
        var start = world.AttributedLog.Count;

        int enableLine;
        using (world.BeginDelivery())
        {
            enableLine = Enable(world, holder);
            world.Set(holder, new Burning { Ticks = 1 });
        }
        world.Playback();

        var credited = new List<(LogKind, int)>();
        for (var i = start; i < world.AttributedLog.Count; i++)
        {
            var entry = world.AttributedLog[i];
            if (entry.Entry.Is<Burning>())
                credited.Add((entry.Entry.Kind, entry.ChangedBy.Line));
        }
        await Assert.That(credited).IsEquivalentTo([(LogKind.Enabled, enableLine)]);
    }

    static readonly QueryDesc Healthy = QueryDesc.Builder().Read<Health>().Build();

    [Test]
    public async Task A_reparent_a_batch_left_waiting_leaves_later_writes_immediate_after_playback()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        var child = world.Create();
        using (world.Batch(child))
            world.SetParent(child, parent);
        world.Playback();

        world.Set(child, new Health { Value = 1 });

        await Assert.That(world.Query(Healthy).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task An_edge_to_an_entity_created_and_destroyed_by_an_observer_leaves_later_writes_immediate()
    {
        using var world = TestWorlds.Create();
        var source = world.Create();
        using (world.BeginDelivery())
        {
            var created = world.Create();
            world.Relate(source, created, new Follows());
            world.Destroy(created);
        }
        world.Playback();

        world.Set(source, new Health { Value = 1 });

        await Assert.That(world.Query(Healthy).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Lifecycle_calls_on_an_entity_with_changes_waiting_keep_their_order_with_them()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        var other = world.Create();
        var child = world.Create();
        world.SetParent(child, parent);
        using (world.Batch(child))
            world.ClearParent(child);
        Entity doomed;
        using (world.BeginDelivery())
        {
            doomed = world.Create();
            world.Destroy(doomed);
        }
        world.SetParent(child, other);
        var cancelled = world.CancelDestroy(doomed);
        world.Playback();

        await Assert
            .That((world.Parent(child), cancelled, world.IsLive(doomed)))
            .IsEqualTo((other, true, true));
    }

    [Test]
    public async Task A_destroy_after_a_recorded_cancel_still_destroys()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Destroy(entity);
        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.CancelDestroy(entity);
        buffer.Commands.Destroy(entity);
        world.Submit(buffer);
        world.Playback(finalizeDestroys: false);

        await Assert.That(world.StateOf(entity)).IsEqualTo(LifecycleState.PendingDestroy);
    }

    [Test]
    public async Task Edge_writes_after_a_waiting_edge_keep_their_order_from_either_end()
    {
        using var world = TestWorlds.Create();
        var a = world.Create();
        var old = world.Create();
        world.Relate<Follows>(a, old);
        Entity retargeted,
            allied;
        using (world.BeginDelivery())
        {
            retargeted = world.Create();
            world.Retarget<Follows>(a, old, retargeted);
            world.ClearSources<Follows>(old);
            allied = world.Create();
            world.Relate<AlliedWith>(a, allied);
            world.ClearSources<AlliedWith>(a);
        }
        world.Playback();

        await Assert
            .That(
                (
                    world.HasRelation<Follows>(a, retargeted),
                    world.HasRelation<AlliedWith>(a, allied)
                )
            )
            .IsEqualTo((true, false));
    }
}
