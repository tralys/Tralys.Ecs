namespace Tralys.Ecs.Tests;

public sealed class WatchGroundTruthTests
{
    [Flags]
    public enum Owed : byte
    {
        None = 0,
        Health = 1 << 0,
        Whole = 1 << 1,
        All = Health | Whole,
    }

    sealed class Scope : IWatchScope<Owed>
    {
        static readonly QueryDesc Healthy = QueryDesc.Builder().Read<Health>().Build();

        public static void Everything(World world, ChangeSet<Owed> into)
        {
            foreach (var chunk in world.Query(Healthy))
            foreach (var row in chunk.Rows)
                into.Mark(chunk.Entities[row], Owed.All);
        }

        public static Owed Whole(World world, Entity entity) =>
            Admitted(world, entity)
                ? Owed.All
                : throw new InvalidOperationException(
                    $"Whole called on {entity}, {world.StateOf(entity)}, detached {world.IsDetached(entity)}."
                );
    }

    static bool Admitted(World world, Entity entity) =>
        world.IsLive(entity) && !world.IsDetached(entity);

    sealed class Model
    {
        public readonly List<string> Failures = [];
        public int Resets;
        public int Clears;
        public int Overflows;
        public int Resyncs;
        public int StaleCommits;
    }

    [Test]
    public async Task A_watch_and_a_cursor_match_the_model_across_resets_clears_and_overflows()
    {
        var model = Run(seed: 1, 40_000);

        await Assert.That(model.Failures).IsEmpty();
        await Assert
            .That(
                (
                    model.Resets > 0,
                    model.Clears > 0,
                    model.Overflows > 0,
                    model.Resyncs > 0,
                    model.StaleCommits > 0
                )
            )
            .IsEqualTo((true, true, true, true, true));
    }

    static Model Run(int seed, int operations)
    {
        using var world = TestWorlds.Create(new WorldOptions { WatchQueueCap = 4 });
        var random = new Random(seed);
        var model = new Model();
        var watch = world.Changes.Watch<Owed, Scope>(
            "model",
            static w => w.Component<Health>(Owed.Health)
        );
        var cursor = world.Changes.Register("model");
        var owed = new ChangeSet<Owed>();
        var expected = new Dictionary<Entity, Owed>();
        var touched = new HashSet<Entity>();
        var resynced = new HashSet<Entity>();
        var sinceCommit = new HashSet<Entity>();
        var sinceSample = new HashSet<Entity>();
        var cursorAll = false;
        var watchEpochChanged = false;
        var resetSinceCollect = false;
        ChangeWindow? held = null;
        var heldEpochChanged = false;
        var live = new List<Entity>();
        var snapshots = new List<(WorldSnapshot Snapshot, Entity[] Live)>();
        var op = 0;

        void Stamped(Entity entity)
        {
            touched.Add(entity);
            sinceCommit.Add(entity);
            sinceSample.Add(entity);
        }

        void EpochChanged(bool reset)
        {
            watchEpochChanged = true;
            resetSinceCollect |= reset;
            heldEpochChanged = true;
            touched.Clear();
            resynced.Clear();
            sinceCommit.Clear();
            sinceSample.Clear();
            cursorAll = reset;
        }

        void Fail(string message) => model.Failures.Add($"op {op}: {message}");

        void Collect()
        {
            var overflowed = watch.queue.Overflowed;
            try
            {
                watch.Collect(owed);
            }
            catch (InvalidOperationException error)
            {
                Fail($"collect threw: {error.Message}");
                return;
            }
            if (overflowed && !resetSinceCollect)
                model.Overflows++;
            if (watchEpochChanged)
                expected.Clear();
            foreach (var gone in expected.Keys.Where(entity => !world.Exists(entity)).ToList())
                expected.Remove(gone);
            void Owe(Entity entity, Owed bits) =>
                expected[entity] = expected.GetValueOrDefault(entity) | bits;
            if (overflowed)
            {
                foreach (var entity in live)
                {
                    if (Admitted(world, entity) && world.Has<Health>(entity))
                        Owe(entity, Owed.All);
                }
            }
            else
            {
                foreach (var entity in touched)
                {
                    if (Admitted(world, entity))
                        Owe(entity, Owed.Health);
                }
                foreach (var entity in resynced)
                {
                    if (Admitted(world, entity))
                        Owe(entity, Owed.All);
                }
            }
            touched.Clear();
            resynced.Clear();
            watchEpochChanged = false;
            resetSinceCollect = false;

            var actual = new Dictionary<Entity, Owed>();
            foreach (var (entity, bits) in owed)
                actual[entity] = bits;
            var wrong = actual
                .Keys.Union(expected.Keys)
                .Where(entity =>
                    actual.GetValueOrDefault(entity) != expected.GetValueOrDefault(entity)
                )
                .Select(entity =>
                    $"{entity} marked {actual.GetValueOrDefault(entity)}, owed {expected.GetValueOrDefault(entity)}"
                )
                .ToList();
            if (wrong.Count > 0)
                Fail($"watch (overflowed {overflowed}): {string.Join("; ", wrong)}");

            foreach (var entity in actual.Keys.Where(_ => random.Next(2) == 0))
            {
                owed.Forget(entity);
                expected.Remove(entity);
            }
        }

        void SampleCursor()
        {
            var read = world.Changes.Sample(cursor);
            var seen = new HashSet<Entity>();
            foreach (var row in read.Changed<Health>())
                seen.Add(row.Entity);
            var visible = live.Where(entity =>
                    Admitted(world, entity)
                    && world.Has<Health>(entity)
                    && (cursorAll || sinceCommit.Contains(entity))
                )
                .ToHashSet();
            if (!seen.SetEquals(visible))
                Fail(
                    $"cursor (all {cursorAll}): missing {string.Join(",", visible.Except(seen))}, extra {string.Join(",", seen.Except(visible))}"
                );
            held = read.Window;
            heldEpochChanged = false;
            sinceSample.Clear();
        }

        void CommitCursor(ChangeWindow window)
        {
            world.Changes.Commit(cursor, window);
            held = null;
            if (heldEpochChanged)
            {
                model.StaleCommits++;
                return;
            }
            sinceCommit.Clear();
            sinceCommit.UnionWith(sinceSample);
            cursorAll = false;
        }

        for (; op < operations && model.Failures.Count == 0; op++)
        {
            var entity = live.Count > 0 ? live[random.Next(live.Count)] : default;
            var usable = !entity.IsDefault && world.IsLive(entity);
            switch (random.Next(22))
            {
                case 0 or 1 when live.Count < 48:
                {
                    var created = world.Create();
                    live.Add(created);
                    if (random.Next(3) > 0)
                    {
                        world.Set(created, new Health { Value = random.Next(3) });
                        Stamped(created);
                    }
                    break;
                }
                case 2 or 3 when usable && world.Has<Health>(entity):
                {
                    var next = random.Next(3);
                    if (world.Get<Health>(entity).Value != next)
                        Stamped(entity);
                    world.Set(entity, new Health { Value = next });
                    break;
                }
                case 4 when usable && world.Has<Health>(entity):
                {
                    using var edit = world.Edit<Health>(entity);
                    var next = random.Next(3);
                    if (edit.Value.Value != next)
                        Stamped(entity);
                    edit.Value.Value = next;
                    break;
                }
                case 5 when usable && world.Has<Health>(entity):
                    world.Remove<Health>(entity);
                    touched.Add(entity);
                    break;
                case 6 when usable && !world.Has<Health>(entity):
                    world.Set(entity, new Health { Value = random.Next(3) });
                    Stamped(entity);
                    break;
                case 7 when usable:
                    world.Destroy(entity);
                    break;
                case 8:
                    world.Playback();
                    live.RemoveAll(e => !world.Exists(e));
                    break;
                case 9 when usable && !world.IsDetached(entity):
                    world.Detach(entity);
                    break;
                case 10 when usable && world.IsDetached(entity):
                    world.Reattach(entity);
                    resynced.Add(entity);
                    model.Resyncs++;
                    break;
                case 11
                    when !entity.IsDefault
                        && world.StateOf(entity) == LifecycleState.PendingDestroy:
                    world.CancelDestroy(entity);
                    resynced.Add(entity);
                    model.Resyncs++;
                    break;
                case 12 when snapshots.Count < 4:
                    snapshots.Add((world.Snapshot(), [.. live]));
                    break;
                case 13 when snapshots.Count > 0 && random.Next(4) == 0:
                {
                    var chosen = random.Next(snapshots.Count);
                    world.Reset(snapshots[chosen].Snapshot);
                    live.Clear();
                    live.AddRange(snapshots[chosen].Live);
                    EpochChanged(reset: true);
                    model.Resets++;
                    break;
                }
                case 14 when random.Next(16) == 0:
                    world.Clear();
                    live.Clear();
                    EpochChanged(reset: false);
                    model.Clears++;
                    break;
                case 15 or 16 or 17:
                    Collect();
                    break;
                case 18 or 19:
                    if (held is { } window)
                        CommitCursor(window);
                    else
                        SampleCursor();
                    break;
                case 20:
                    world.AdvanceVersion();
                    break;
                case 21:
                    foreach (var churned in live.Where(_ => random.Next(3) == 0).ToList())
                    {
                        if (!world.IsLive(churned) || !world.Has<Health>(churned))
                            continue;
                        world.Remove<Health>(churned);
                        world.Set(churned, new Health { Value = random.Next(3) });
                        Stamped(churned);
                    }
                    break;
            }
        }
        return model;
    }
}
