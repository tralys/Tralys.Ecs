namespace Tralys.Ecs.Tests;

public sealed class ChangedRowsTests
{
    [Test]
    public async Task Every_cursor_sees_exactly_the_rows_stamped_since_its_last_commit()
    {
        var failures = Run(seed: 1, 50_000);

        await Assert.That(failures).IsEmpty();
    }

    sealed class Expected(CursorId cursor)
    {
        public readonly CursorId Cursor = cursor;
        public readonly HashSet<(Entity, int)> Stamped = [];
        public readonly HashSet<(Entity, int)> Added = [];
    }

    static List<string> Run(int seed, int operations)
    {
        using var world = TestWorlds.Create();
        var random = new Random(seed);
        Expected[] cursors =
        [
            new(world.Changes.Register("fast")),
            new(world.Changes.Register("slow")),
            new(world.Changes.Register("skimming")),
        ];
        var live = new List<Entity>();
        var failures = new List<string>();

        void Stamped(Entity owner, int kind, bool added)
        {
            foreach (var expected in cursors)
            {
                expected.Stamped.Add((owner, kind));
                if (added)
                    expected.Added.Add((owner, kind));
            }
        }

        void Gone(Func<(Entity Owner, int Kind), bool> row)
        {
            foreach (var expected in cursors)
            {
                expected.Stamped.RemoveWhere(r => row(r));
                expected.Added.RemoveWhere(r => row(r));
            }
        }

        for (var op = 0; op < operations; op++)
        {
            if (op % 40 == 0)
                world.AdvanceVersion();

            var owner = live.Count > 0 ? live[random.Next(live.Count)] : default;
            var kind = random.Next(4);
            var stacks = random.Next(3);
            switch (random.Next(8))
            {
                case 0:
                    live.Add(world.Create());
                    break;
                case 1 when live.Count > 0:
                {
                    var rows = world.EffectRows(owner);
                    var had = rows.TryGet(kind, out var old);
                    if (!had || old.Stacks != stacks)
                        Stamped(owner, kind, added: !had);
                    rows.Set(new Effect { Kind = kind, Stacks = stacks });
                    break;
                }
                case 2 when live.Count > 0 && world.EffectRows(owner).Contains(kind):
                {
                    using var edit = world.EffectRows(owner).Edit(kind);
                    if (edit.Value.Stacks != stacks)
                        Stamped(owner, kind, added: false);
                    edit.Value.Stacks = stacks;
                    break;
                }
                case 3 when live.Count > 0:
                {
                    using var edit = world.EffectRows(owner).EditOrAdd(kind, out var added);
                    if (added || edit.Value.Stacks != stacks)
                        Stamped(owner, kind, added);
                    edit.Value.Stacks = stacks;
                    break;
                }
                case 4 when live.Count > 0:
                    if (world.EffectRows(owner).Remove(kind))
                        Gone(r => r == (owner, kind));
                    break;
                case 5 when live.Count > 0:
                    world.Destroy(owner);
                    live.Remove(owner);
                    Gone(r => r.Owner == owner);
                    break;
                case 6:
                    world.Playback();
                    break;
            }

            if (op % 7 == 3)
                Sample(world, cursors[0], op, failures);
            if (op % 29 == 5)
                Sample(world, cursors[1], op, failures);
            if (op % 11 == 8)
                Sample(world, cursors[2], op, failures, walk: op % 33 == 8);
            if (failures.Count > 0)
                break;
        }
        return failures;
    }

    static void Sample(
        World world,
        Expected expected,
        int op,
        List<string> failures,
        bool walk = true
    )
    {
        var read = world.Changes.Sample(expected.Cursor);
        if (!walk)
        {
            world.Changes.Commit(expected.Cursor, read.Window);
            expected.Stamped.Clear();
            expected.Added.Clear();
            return;
        }
        var reported = 0;
        var stamped = new HashSet<(Entity, int)>();
        var added = new HashSet<(Entity, int)>();
        foreach (var row in read.ChangedRows<int, Effect>())
        {
            reported++;
            stamped.Add((row.Entity, row.Key));
            if (row.Added)
                added.Add((row.Entity, row.Key));
        }
        if (
            reported != stamped.Count
            || !stamped.SetEquals(expected.Stamped)
            || !added.SetEquals(expected.Added)
        )
            failures.Add(
                $"op {op}, cursor {world.Changes.NameOf(expected.Cursor)}: reported {reported}, expected {expected.Stamped.Count}; "
                    + $"missing {string.Join(",", expected.Stamped.Except(stamped))}, extra {string.Join(",", stamped.Except(expected.Stamped))}, "
                    + $"added {string.Join(",", added)} vs {string.Join(",", expected.Added)}"
            );
        world.Changes.Commit(expected.Cursor, read.Window);
        expected.Stamped.Clear();
        expected.Added.Clear();
    }

    [Test]
    public async Task Untracked_row_writes_are_never_reported()
    {
        using var world = TestWorlds.Create();
        var seeded = world.Create();
        world.EffectRows(seeded).Set(new Effect { Kind = 1 });
        var cursor = world.Changes.Register("rows");

        using (world.Untracked())
        {
            world.EffectRows(seeded).Set(new Effect { Kind = 2, Stacks = 1 });
            using var edit = world.EffectRows(seeded).Edit(1);
            edit.Value.Stacks = 3;
        }

        var sample = world.Changes.Sample(cursor);
        var reported = new List<Entity>();
        foreach (var row in sample.ChangedRows<int, Effect>())
            reported.Add(row.Entity);

        await Assert.That(reported).IsEmpty();
    }

    [Test]
    public async Task A_recorded_row_write_stamps_as_the_scope_it_was_written_in_does()
    {
        using var world = TestWorlds.Create();
        var cursor = world.Changes.Register("rows");
        Entity hidden,
            shown;
        using (world.BeginDelivery())
        {
            hidden = world.Create();
            shown = world.Create();
            using (world.Untracked())
                world.EffectRows(hidden).Set(new Effect { Kind = 1 });
            world.EffectRows(shown).Set(new Effect { Kind = 1 });
        }
        world.Playback();

        var sample = world.Changes.Sample(cursor);
        var reported = new List<Entity>();
        foreach (var row in sample.ChangedRows<int, Effect>())
            reported.Add(row.Entity);
        var seeded = world.EffectRows(hidden).Contains(1);

        await Assert.That(seeded).IsTrue();
        await Assert.That(reported).IsEquivalentTo([shown]);
    }

    [Test]
    public async Task A_walk_started_while_another_is_open_is_refused_and_the_open_walk_misses_nothing()
    {
        using var world = TestWorlds.Create();
        world.EffectRows(world.Create()).Set(new Effect { Kind = 1 });
        var cursor = world.Changes.Register("rows");
        var owners = new List<Entity>();
        for (var i = 0; i < 3; i++)
        {
            world.AdvanceVersion();
            var owner = world.Create();
            world.EffectRows(owner).Set(new Effect { Kind = 1 });
            world.EffectRows(owner).Set(new Effect { Kind = 2 });
            owners.Add(owner);
        }

        var sample = world.Changes.Sample(cursor);
        var reported = new HashSet<Entity>();
        var refused = false;
        foreach (var row in sample.ChangedRows<int, Effect>())
        {
            if (!reported.Add(row.Entity) || reported.Count > 1)
                continue;
            using (var edit = world.EffectRows(owners[0]).Edit(2))
                edit.Value.Stacks = 2;
            try
            {
                foreach (var _ in sample.ChangedRows<int, Effect>()) { }
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
        }

        await Assert.That(refused).IsTrue();
        await Assert.That(reported).IsEquivalentTo(owners);
    }

    [Test]
    public async Task A_reset_brings_back_which_owners_have_stamped_rows()
    {
        using var world = TestWorlds.Create();
        var cursor = world.Changes.Register("rows");
        var first = world.Create();
        var second = world.Create();
        var third = world.Create();
        world.EffectRows(first).Set(new Effect { Kind = 1 });
        world.EffectRows(second).Set(new Effect { Kind = 1 });
        Owners(world, cursor);
        world.EffectRows(third).Set(new Effect { Kind = 1 });
        var snapshot = world.Snapshot();
        world.EffectRows(world.Create()).Set(new Effect { Kind = 1 });
        world.Destroy(first);
        world.Playback();
        Owners(world, cursor);

        world.Reset(snapshot);

        await Assert.That(Owners(world, cursor)).IsEquivalentTo([first, second, third]);
    }

    static List<Entity> Owners(World world, CursorId cursor)
    {
        var sample = world.Changes.Sample(cursor);
        var owners = new List<Entity>();
        foreach (var row in sample.ChangedRows<int, Effect>())
            owners.Add(row.Entity);
        world.Changes.Commit(cursor, sample.Window);
        return owners;
    }

    [Test]
    public async Task A_tracked_row_write_recorded_then_played_back_inside_untracked_still_stamps()
    {
        using var world = TestWorlds.Create();
        var cursor = world.Changes.Register("rows");
        Entity owner;
        using (world.BeginDelivery())
        {
            owner = world.Create();
            world.EffectRows(owner).Set(new Effect { Kind = 1 });
        }
        using (world.Untracked())
            world.Playback();

        var reported = new List<Entity>();
        foreach (var row in world.Changes.Sample(cursor).ChangedRows<int, Effect>())
            reported.Add(row.Entity);
        await Assert.That(reported).IsEquivalentTo([owner]);
    }
}
