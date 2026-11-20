namespace Tralys.Ecs.Tests;

public sealed class ChangeWatchTests
{
    [Flags]
    public enum Owed : byte
    {
        None = 0,
        Health = 1 << 0,
        Rows = 1 << 1,
        Followers = 1 << 2,
        Items = 1 << 3,
        All = Health | Rows | Followers | Items,
    }

    sealed class EveryHealth : IWatchScope<Owed>
    {
        static readonly QueryDesc Healthy = QueryDesc.Builder().Read<Health>().Build();

        public static void Everything(World world, ChangeSet<Owed> into)
        {
            foreach (var chunk in world.Query(Healthy))
            foreach (var row in chunk.Rows)
                into.Mark(chunk.Entities[row], Owed.All);
        }

        public static Owed Whole(World world, Entity entity) => Owed.All;
    }

    [Test]
    public async Task An_entity_changed_while_detached_is_marked_whole_once_it_is_back()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Health { Value = 1 });
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "reentry",
            static w => w.Component<Health>(Owed.Health)
        );
        var owed = new ChangeSet<Owed>();

        world.Detach(entity);
        world.Set(entity, new Health { Value = 2 });
        world.Reattach(entity);
        watch.Collect(owed);

        await Assert.That(owed.MarksOf(entity)).IsEqualTo(Owed.All);
    }

    [Test]
    public async Task A_change_to_a_detached_entity_marks_nothing_while_it_is_away()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Health { Value = 1 });
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "away",
            static w => w.Component<Health>(Owed.Health)
        );
        var owed = new ChangeSet<Owed>();

        world.Detach(entity);
        world.Remove<Health>(entity);
        watch.Collect(owed);

        await Assert.That(owed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_overflowed_watch_marks_everything_its_scope_names()
    {
        using var world = TestWorlds.Create(new WorldOptions { WatchQueueCap = 8 });
        var untouched = world.Create();
        world.Set(untouched, new Health { Value = 1 });
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "overflow",
            static w => w.Component<Health>(Owed.Health)
        );
        var owed = new ChangeSet<Owed>();

        Churn(world, 20);
        watch.Collect(owed);

        await Assert.That(owed.MarksOf(untouched)).IsEqualTo(Owed.All);
    }

    [Test]
    public async Task One_watch_overflowing_leaves_another_exact()
    {
        using var world = TestWorlds.Create(new WorldOptions { WatchQueueCap = 8 });
        var untouched = world.Create();
        world.Set(untouched, new Health { Value = 1 });
        using var busy = world.Changes.Watch<Owed, EveryHealth>(
            "busy",
            static w => w.Component<Health>(Owed.Health)
        );
        var quiet = world.Changes.Watch<Owed, EveryHealth>(
            "quiet",
            static w => w.Rows<int, Effect>(Owed.Rows)
        );
        var owed = new ChangeSet<Owed>();

        Churn(world, 20);
        quiet.Collect(owed);

        await Assert.That(owed.Count).IsEqualTo(0);
    }

    static void Churn(World world, int removals)
    {
        for (var i = 0; i < removals; i++)
        {
            var entity = world.Create();
            world.Set(entity, new Health { Value = 1 });
            world.Remove<Health>(entity);
        }
    }

    [Test]
    public async Task A_snapshot_reset_marks_everything_and_a_clear_does_not()
    {
        using var world = TestWorlds.Create();
        var kept = world.Create();
        world.Set(kept, new Health { Value = 1 });
        var baseline = world.Snapshot();
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "reset",
            static w => w.Component<Health>(Owed.Health)
        );
        var owed = new ChangeSet<Owed>();

        world.Reset(baseline);
        watch.Collect(owed);
        var afterReset = owed.MarksOf(kept);
        owed.Clear();
        world.Clear();
        var fresh = world.Create();
        world.Set(fresh, new Health { Value = 2 });
        watch.Collect(owed);

        await Assert.That(afterReset).IsEqualTo(Owed.All);
        await Assert.That(owed.MarksOf(fresh)).IsEqualTo(Owed.Health);
    }

    [Test]
    public async Task A_watch_queues_only_the_changes_it_acts_on()
    {
        using var world = TestWorlds.Create();
        var health = world.Changes.Watch<Owed, EveryHealth>(
            "health",
            static w => w.Component<Health>(Owed.Health)
        );
        using var rows = world.Changes.Watch<Owed, EveryHealth>(
            "rows",
            static w => w.Rows<int, Effect>(Owed.Rows)
        );

        var entity = world.Create();
        world.Set(entity, new Health { Value = 1 });
        world.Remove<Health>(entity);
        world.EffectRows(entity).Set(new Effect { Kind = 1 });
        world.EffectRows(entity).Remove(1);
        world.Destroy(entity);
        world.Playback();

        await Assert
            .That(health.queue.Events.Select(change => change.Kind))
            .IsEquivalentTo(
                [LogKind.Removed, LogKind.Destroyed],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
    }

    [Test]
    public async Task Disposing_the_last_watch_on_a_type_stops_its_changes_being_wanted()
    {
        using var world = TestWorlds.Create();
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "disposed",
            static w => w.Component<Health>(Owed.Health)
        );

        watch.Dispose();

        await Assert
            .That(world.changes.Wants(world.registry.IndexOf<Health>(), LogKind.Removed))
            .IsFalse();
    }

    [Test]
    public async Task Presence_marks_an_untracked_tag_added_and_removed()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "presence",
            static w => w.Presence<Npc>(Owed.Followers)
        );
        var owed = new ChangeSet<Owed>();

        world.Add<Npc>(entity);
        watch.Collect(owed);
        var added = owed.IsMarked(entity, Owed.Followers);
        owed.Clear();
        world.Remove<Npc>(entity);
        watch.Collect(owed);

        await Assert.That(added).IsTrue();
        await Assert.That(owed.IsMarked(entity, Owed.Followers)).IsTrue();
    }

    [Test]
    public async Task A_removed_row_marks_its_owner()
    {
        using var world = TestWorlds.Create();
        var owner = world.Create();
        world.EffectRows(owner).Set(new Effect { Kind = 1 });
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "rows",
            static w => w.Rows<int, Effect>(Owed.Rows)
        );
        var owed = new ChangeSet<Owed>();

        world.EffectRows(owner).Remove(1);
        watch.Collect(owed);

        await Assert.That(owed.IsMarked(owner, Owed.Rows)).IsTrue();
    }

    [Test]
    public async Task A_row_seeded_inside_untracked_is_not_reported()
    {
        using var world = TestWorlds.Create();
        var owner = world.Create();
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "seed",
            static w => w.Rows<int, Effect>(Owed.Rows)
        );
        var owed = new ChangeSet<Owed>();

        using (world.Untracked())
            world.EffectRows(owner).Set(new Effect { Kind = 1 });
        watch.Collect(owed);

        await Assert.That(owed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_sources_hop_marks_each_source_and_not_the_target_that_changed()
    {
        using var world = TestWorlds.Create();
        var leader = world.Create();
        world.Set(leader, new Health { Value = 1 });
        var first = world.Create();
        var second = world.Create();
        world.Relate<Follows>(first, leader);
        world.Relate<Follows>(second, leader);
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "hop",
            static w => w.Component<Health>(Owed.Followers, Hop.Sources<Follows>())
        );
        var owed = new ChangeSet<Owed>();

        world.Set(leader, new Health { Value = 2 });
        watch.Collect(owed);

        await Assert.That(owed.IsMarked(first, Owed.Followers)).IsTrue();
        await Assert.That(owed.IsMarked(second, Owed.Followers)).IsTrue();
        await Assert.That(owed.IsMarked(leader, Owed.All)).IsFalse();
    }

    [Test]
    public async Task A_reparented_child_marks_the_owner_it_left_and_the_one_it_joined()
    {
        using var world = TestWorlds.Create();
        var losing = world.Create();
        world.Add<Npc>(losing);
        var gaining = world.Create();
        world.Add<Npc>(gaining);
        var item = world.Create();
        world.SetParent(item, losing);
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "descendants",
            static w => w.Descendants<Npc>(Owed.Items)
        );
        var owed = new ChangeSet<Owed>();
        watch.Collect(owed);
        owed.Clear();

        world.SetParent(item, gaining);
        watch.Collect(owed);

        await Assert.That(owed.IsMarked(losing, Owed.Items)).IsTrue();
        await Assert.That(owed.IsMarked(gaining, Owed.Items)).IsTrue();
    }

    static ChangeWatch<Owed> Descendants(World world) =>
        world.Changes.Watch<Owed, EveryHealth>(
            "descendants",
            static w => w.Descendants<Npc>(Owed.Items)
        );

    [Test]
    public async Task A_raise_just_before_a_watch_registers_does_not_hide_the_next()
    {
        using var world = TestWorlds.Create();
        var owner = world.Create();
        world.Add<Npc>(owner);
        world.SetParent(world.Create(), owner);
        var watch = Descendants(world);
        var owed = new ChangeSet<Owed>();

        world.SetParent(world.Create(), owner);
        watch.Collect(owed);

        await Assert.That(owed.IsMarked(owner, Owed.Items)).IsTrue();
    }

    [Test]
    public async Task An_owner_raised_twice_in_one_window_is_queued_once()
    {
        using var world = TestWorlds.Create();
        var owner = world.Create();
        world.Add<Npc>(owner);
        var watch = Descendants(world);

        world.SetParent(world.Create(), owner);
        world.AdvanceVersion();
        world.SetParent(world.Create(), owner);

        await Assert.That(watch.queue.Raised).IsEquivalentTo([owner]);
    }

    [Test]
    public async Task A_detached_owner_is_not_marked_until_it_is_back()
    {
        using var world = TestWorlds.Create();
        var owner = world.Create();
        world.Add<Npc>(owner);
        var item = world.Create();
        world.SetParent(item, owner);
        var watch = Descendants(world);
        var owed = new ChangeSet<Owed>();

        world.Detach(owner);
        world.Destroy(item);
        world.Playback();
        watch.Collect(owed);
        var whileAway = owed.MarksOf(owner);
        world.Reattach(owner);
        watch.Collect(owed);

        await Assert.That(whileAway).IsEqualTo(Owed.None);
        await Assert.That(owed.MarksOf(owner)).IsEqualTo(Owed.All);
    }

    [Test]
    public async Task A_raised_ancestor_without_the_owner_type_is_not_marked()
    {
        using var world = TestWorlds.Create();
        var plain = world.Create();
        var watch = Descendants(world);
        var owed = new ChangeSet<Owed>();

        world.SetParent(world.Create(), plain);
        watch.Collect(owed);

        await Assert.That(owed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_steady_collect_loop_allocates_nothing()
    {
        using var world = TestWorlds.Create();
        var holder = world.Create();
        world.Set(holder, new Health { Value = 1 });
        var first = world.Create();
        world.Add<Npc>(first);
        var second = world.Create();
        world.Add<Npc>(second);
        var item = world.Create();
        world.SetParent(item, first);
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "steady",
            static w => w.Component<Health>(Owed.Health).Descendants<Npc>(Owed.Items)
        );
        var owed = new ChangeSet<Owed>();
        var flip = false;

        var bytes = AllocationAssert.BytesPerCall(() =>
        {
            world.Remove<Health>(holder);
            world.Set(holder, new Health { Value = 1 });
            world.SetParent(item, (flip = !flip) ? second : first);
            watch.Collect(owed);
            owed.Clear();
        });

        await Assert.That(bytes).IsEqualTo(0);
    }
}
