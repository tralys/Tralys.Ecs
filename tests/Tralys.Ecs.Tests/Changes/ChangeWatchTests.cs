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
    public async Task An_expired_watch_marks_everything_its_scope_names()
    {
        using var world = TestWorlds.Create(new WorldOptions { LogRetentionCap = 8 });
        var untouched = world.Create();
        world.Set(untouched, new Health { Value = 1 });
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "expiry",
            static w => w.Component<Health>(Owed.Health)
        );
        var owed = new ChangeSet<Owed>();

        for (var i = 0; i < 20; i++)
            world.Create();
        watch.Collect(owed);

        await Assert.That(owed.MarksOf(untouched)).IsEqualTo(Owed.All);
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
        var owners = QueryDesc.Builder().With<Npc>().Build();
        var watch = world.Changes.Watch<Owed, EveryHealth>(
            "descendants",
            w => w.Descendants(Owed.Items, owners)
        );
        var owed = new ChangeSet<Owed>();
        watch.Collect(owed);
        owed.Clear();

        world.SetParent(item, gaining);
        watch.Collect(owed);

        await Assert.That(owed.IsMarked(losing, Owed.Items)).IsTrue();
        await Assert.That(owed.IsMarked(gaining, Owed.Items)).IsTrue();
    }
}
