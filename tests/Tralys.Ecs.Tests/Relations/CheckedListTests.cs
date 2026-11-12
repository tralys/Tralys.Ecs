namespace Tralys.Ecs.Tests;

public sealed class CheckedListTests
{
    [Test]
    public async Task Reparenting_while_walking_children_throws()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        world.SetParent(world.Create(), parent);
        world.SetParent(world.Create(), parent);

        await Assert
            .That(() =>
            {
                foreach (var child in world.Children(parent))
                    world.ClearParent(child);
            })
            .Throws<InvalidOperationException>()
            .WithMessageContaining("changed after");
    }

    [Test]
    public async Task Unrelating_while_walking_sources_throws()
    {
        using var world = TestWorlds.Create();
        var target = world.Create();
        world.Relate<Follows>(world.Create(), target);
        world.Relate<Follows>(world.Create(), target);

        await Assert
            .That(() =>
            {
                foreach (var source in world.Sources<Follows>(target))
                    world.Unrelate<Follows>(source, target);
            })
            .Throws<InvalidOperationException>()
            .WithMessageContaining("changed after");
    }

    [Test]
    public async Task Unrelating_while_walking_targets_throws()
    {
        using var world = TestWorlds.Create();
        var shared = world.Create();
        world.Relate<Follows>(world.Create(), shared);
        world.Relate<Follows>(world.Create(), shared);
        var source = world.Create();
        var first = world.Create();
        world.Relate<Follows>(source, first);
        world.Relate<Follows>(source, world.Create());

        await Assert
            .That(() =>
            {
                var walked = 0;
                foreach (var target in world.Targets<Follows>(source))
                {
                    if (walked++ == 0)
                        world.Unrelate<Follows>(source, first);
                }
            })
            .Throws<InvalidOperationException>()
            .WithMessageContaining("changed after");
    }

    [Test]
    public async Task Removing_a_row_while_walking_its_owners_throws()
    {
        using var world = TestWorlds.Create();
        world.MembershipRows(world.Create()).Set(new Membership { Guild = 7 });
        world.MembershipRows(world.Create()).Set(new Membership { Guild = 7 });

        await Assert
            .That(() =>
            {
                foreach (var owner in world.RowOwners<int, Membership>(7))
                    world.MembershipRows(owner).Remove(7);
            })
            .Throws<InvalidOperationException>()
            .WithMessageContaining("changed after");
    }

    [Test]
    public async Task Adding_a_child_while_walking_children_throws()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        world.SetParent(world.Create(), parent);

        await Assert
            .That(() =>
            {
                foreach (var child in world.Children(parent))
                    world.SetParent(world.Create(), parent);
            })
            .Throws<InvalidOperationException>()
            .WithMessageContaining("changed after");
    }

    [Test]
    public async Task A_holder_joining_the_other_lane_invalidates_a_list_over_both()
    {
        using var world = TestWorlds.Create();
        var parked = world.Create();
        world.Set(parked, new Placement { Map = 1 });
        world.Detach(parked);

        await Assert
            .That(() =>
            {
                var list = world.PlacementByMap().IncludeDetached().Get(1);
                world.Set(world.Create(), new Placement { Map = 1 });
                _ = list.Count;
            })
            .Throws<InvalidOperationException>()
            .WithMessageContaining("changed after");
    }

    [Test]
    public async Task A_targets_list_reads_a_payload_rewritten_after_the_arena_moved()
    {
        using var world = TestWorlds.Create();
        var source = world.Create();
        var target = world.Create();
        world.Relate(source, target, new Owes { Amount = 1 });
        var targets = world.Targets<Owes>(source);

        var crowd = world.Create();
        for (var i = 0; i < 64; i++)
            world.Relate(crowd, world.Create(), new Owes { Amount = i });
        world.Relate(source, target, new Owes { Amount = 2 });
        var amount = targets.Payload(0).Amount;

        await Assert.That(amount).IsEqualTo(2);
    }

    [Test]
    public async Task A_relation_list_held_across_a_reset_that_drops_the_relation_throws()
    {
        using var world = TestWorlds.Create();
        var target = world.Create();
        var source = world.Create();
        var baseline = world.Snapshot();
        world.Relate<Follows>(source, target);

        await Assert
            .That(() =>
            {
                var sources = world.Sources<Follows>(target);
                world.Reset(baseline);
                _ = sources.Count;
            })
            .Throws<InvalidOperationException>()
            .WithMessageContaining("changed after");
    }

    [Test]
    public async Task A_list_id_keeps_counting_through_reuse_clear_and_load()
    {
        var arena = new ListArena<int>();
        var list = arena.New();
        arena.Add(list, 1);
        var read = arena.StampOf(list);
        arena.RemoveAt(list, 0);
        arena.Free(list);
        var reused = arena.New();
        arena.Add(reused, 2);
        var afterReuse = arena.StampOf(reused);
        arena.Clear();
        var afterClear = arena.StampOf(reused);
        var held = arena.New();
        arena.Add(held, 3);
        var beforeLoad = arena.StampOf(held);
        arena.Load(new ListArena<int>());
        var afterLoad = arena.StampOf(held);

        await Assert.That(reused).IsEqualTo(list);
        await Assert.That(afterReuse).IsNotEqualTo(read);
        await Assert.That(afterClear).IsNotEqualTo(afterReuse);
        await Assert.That(afterLoad).IsNotEqualTo(beforeLoad);
    }

    [Test]
    public async Task Moving_another_key_s_holder_while_walking_an_index_key_is_allowed()
    {
        using var world = TestWorlds.Create();
        world.Set(world.Create(), new Placement { Map = 1 });
        world.Set(world.Create(), new Placement { Map = 1 });
        var other = world.Create();
        world.Set(other, new Placement { Map = 2 });

        var walked = 0;
        foreach (var entity in world.PlacementByMap().Get(1))
            world.Set(other, new Placement { Map = 3 + ++walked });

        await Assert.That(walked).IsEqualTo(2);
    }

    [Test]
    public async Task Dropping_a_detached_holder_while_walking_its_key_throws()
    {
        using var world = TestWorlds.Create();
        world.Set(world.Create(), new Placement { Map = 1 });
        var parked = world.Create();
        world.Set(parked, new Placement { Map = 1 });
        world.Detach(parked);

        await Assert
            .That(() =>
            {
                foreach (var entity in world.PlacementByMap().IncludeDetached().Get(1))
                    world.Remove<Placement>(parked);
            })
            .Throws<InvalidOperationException>()
            .WithMessageContaining("changed after");
    }

    [Test]
    public async Task Reparenting_under_other_parents_while_walking_children_is_allowed()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        world.SetParent(world.Create(), parent);
        world.SetParent(world.Create(), parent);
        var moved = world.Create();
        var parents = (Entity[])[world.Create(), world.Create()];

        var walked = 0;
        foreach (var child in world.Children(parent))
            world.SetParent(moved, parents[walked++]);

        await Assert.That(walked).IsEqualTo(2);
    }

    [Test]
    public async Task Relating_to_another_target_while_walking_sources_is_allowed()
    {
        using var world = TestWorlds.Create();
        var target = world.Create();
        world.Relate<Follows>(world.Create(), target);
        world.Relate<Follows>(world.Create(), target);
        var elsewhere = world.Create();

        var walked = 0;
        foreach (var source in world.Sources<Follows>(target))
        {
            world.Relate<Follows>(world.Create(), elsewhere);
            walked++;
        }

        await Assert.That(walked).IsEqualTo(2);
    }

    [Test]
    public async Task Relating_from_another_source_while_walking_targets_is_allowed()
    {
        using var world = TestWorlds.Create();
        var source = world.Create();
        world.Relate<Follows>(source, world.Create());
        world.Relate<Follows>(source, world.Create());
        var elsewhere = world.Create();

        var walked = 0;
        foreach (var target in world.Targets<Follows>(source))
        {
            world.Relate<Follows>(elsewhere, world.Create());
            walked++;
        }

        await Assert.That(walked).IsEqualTo(2);
    }

    [Test]
    public async Task Adding_a_row_under_another_key_while_walking_owners_is_allowed()
    {
        using var world = TestWorlds.Create();
        world.MembershipRows(world.Create()).Set(new Membership { Guild = 7 });
        world.MembershipRows(world.Create()).Set(new Membership { Guild = 7 });
        var other = world.Create();

        var walked = 0;
        foreach (var owner in world.RowOwners<int, Membership>(7))
            world.MembershipRows(other).Set(new Membership { Guild = 8 + walked++ });

        await Assert.That(walked).IsEqualTo(2);
    }

    [Test]
    public async Task TryGetRelation_reads_the_payload_of_that_edge()
    {
        using var world = TestWorlds.Create();
        var source = world.Create();
        var first = world.Create();
        var second = world.Create();
        world.Relate(source, first, new Owes { Amount = 1 });
        world.Relate(source, second, new Owes { Amount = 2 });

        var found = world.TryGetRelation<Owes>(source, second, out var owed);

        await Assert.That((found, owed.Amount)).IsEqualTo((true, 2));
    }
}
