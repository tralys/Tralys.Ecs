namespace Tralys.Ecs.Tests;

public sealed partial class IndexModelTests
{
    const int Codes = 5;
    const int Zones = 3;
    const int Dues = 6;

    [Query]
    static void Shift(ref Lot lot, [Arg] int code, [Arg] int zone)
    {
        if (lot.Code == code)
            lot.Zone = zone;
    }

    [Query(Parallel = true)]
    static void Rotate(Entity entity, ref Lot lot, Commands commands, [Arg] int salt)
    {
        switch ((entity.Index * 31u + (uint)salt) % 7)
        {
            case 0:
                lot.Code = (lot.Code + 1) % Codes;
                break;
            case 1:
                lot.Due--;
                break;
            case 2:
                commands.Remove<Lot>(entity);
                break;
            case 3:
                commands.Set(commands.Create(), lot);
                break;
        }
    }

    [Query(Parallel = true)]
    static void Defer(Entity entity, in Position position, Commands commands, [Arg] int salt)
    {
        switch ((entity.Index * 17u + (uint)salt) % 9)
        {
            case 0:
                commands.Remove<Lot>(entity);
                break;
            case 1:
                commands.Set(
                    commands.Create(),
                    new Lot
                    {
                        Code = salt % Codes,
                        Zone = salt % Zones,
                        Due = salt % Dues,
                    }
                );
                break;
            case 2:
                commands.Set(entity, new SparseLot { Code = salt % Codes });
                break;
        }
    }

    [Query]
    static void Bump(ref SparseLot lot, [Arg] int from, [Arg] int to)
    {
        if (lot.Code == from)
            lot.Code = to;
    }

    [Query, IncludePendingDestroy, IncludeDetached]
    static void Gather(Entity entity, in Lot lot, [Arg] List<Entity> into) => into.Add(entity);

    [Query, IncludePendingDestroy, IncludeDetached]
    static void GatherSparse(Entity entity, in SparseLot lot, [Arg] List<Entity> into) =>
        into.Add(entity);

    [Test]
    public async Task Every_index_matches_world_state_through_random_writes()
    {
        using var world = TestWorlds.Create(
            new WorldOptions { Executor = new ShuffledExecutor(seed: 3) }
        );
        var random = new Random(11);
        var known = new List<Entity>();
        var seen = new HashSet<Entity>();
        var found = new List<Entity>();
        var model = new Model(world);
        string? failure = null;
        for (var step = 0; step < 3000 && failure is null; step++)
        {
            found.Clear();
            var op = Step(world, random, known, found);
            RunGather(world, found);
            RunGatherSparse(world, found);
            foreach (var entity in found)
            {
                if (seen.Add(entity))
                    known.Add(entity);
            }
            if (model.Check(known) is { } problem)
                failure = $"step {step}, {op}: {problem}";
        }

        await Assert.That(failure).IsNull();
    }

    static string Step(World world, Random random, List<Entity> known, List<Entity> found)
    {
        var target = known.Count > 0 ? known[random.Next(known.Count)] : default;
        var state = world.StateOf(target);
        var live = state != LifecycleState.Dead;
        var lot = new Lot
        {
            Code = random.Next(Codes),
            Zone = random.Next(Zones),
            Due = random.Next(-1, Dues),
        };
        switch (random.Next(17))
        {
            case 0:
                var created = world.Create();
                world.Set(created, new Position());
                world.Set(created, lot);
                found.Add(created);
                return "create";
            case 1:
                found.Add(world.Create(new LotBundle { Lot = lot }));
                return "create bundle";
            case 2 when live:
                world.Set(target, lot);
                return "set";
            case 3 when live && world.Has<Lot>(target):
            {
                using var edit = world.Edit<Lot>(target);
                if (random.Next(3) == 0)
                    edit.Value.Code = lot.Code;
                else if (random.Next(2) == 0)
                    edit.Value.Zone = lot.Zone;
                else
                    edit.Value.Due = lot.Due;
                return "edit";
            }
            case 4 when live:
                world.Remove<Lot>(target);
                return "remove";
            case 5 when live && state != LifecycleState.PendingDestroy:
                world.Destroy(target);
                return "destroy";
            case 6 when world.allocator.Slots[target.Index].State == LifecycleState.PendingDestroy:
                world.CancelDestroy(target);
                return "cancel destroy";
            case 7 when state == LifecycleState.Alive && !world.IsDetached(target):
                world.Detach(target);
                return "detach";
            case 8 when state == LifecycleState.Alive && world.IsDetached(target):
                world.Reattach(target);
                return "reattach";
            case 9 when live:
            {
                var at = known.IndexOf(target);
                var parent = at > 0 && random.Next(4) > 0 ? known[random.Next(at)] : default;
                if (world.Exists(parent))
                    world.SetParent(target, parent);
                else
                    world.ClearParent(target);
                return "set parent";
            }
            case 10:
                RunShift(world, lot.Code, lot.Zone);
                return "serial ref";
            case 11:
                RunRotate(world, random.Next());
                return "parallel ref";
            case 12:
                RunDefer(world, random.Next());
                return "deferred commands";
            case 13:
                world.Playback();
                return "playback";
            case 14:
                world.Playback();
                return "finalising playback";
            case 15 when live:
                if (random.Next(2) == 0)
                    world.Set(target, new SparseLot { Code = lot.Code });
                else
                    world.Remove<SparseLot>(target);
                return "sparse";
            case 16:
                RunBump(world, lot.Code, random.Next(Codes));
                return "sparse ref";
            default:
                return "nothing";
        }
    }

    sealed class Model(World world)
    {
        readonly Dictionary<Entity, (int, int)> codes = [];
        readonly Dictionary<Entity, ((Entity, int), int)> parented = [];
        readonly Dictionary<Entity, (int, int)> sparseCodes = [];
        readonly Dictionary<(int, int), Entity> codeHolders = [];
        readonly Dictionary<((Entity, int), int), Entity> parentedHolders = [];
        readonly Dictionary<(int, int), Entity> sparseHolders = [];

        public string? Check(List<Entity> known)
        {
            var lots = new List<(Entity Entity, Lot Lot, int Lane)>();
            var nowCodes = new Dictionary<Entity, (int, int)>();
            var nowParented = new Dictionary<Entity, ((Entity, int), int)>();
            var nowSparse = new Dictionary<Entity, (int, int)>();
            foreach (var entity in known)
            {
                var lane = Lane(entity);
                if (lane < 0)
                    continue;
                if (world.TryGet<Lot>(entity, out var lot))
                {
                    lots.Add((entity, lot, lane));
                    nowCodes[entity] = (lot.Code, lane);
                    nowParented[entity] = ((world.Parent(entity), lot.Code), lane);
                }
                if (world.TryGet<SparseLot>(entity, out var sparse))
                    nowSparse[entity] = (sparse.Code, lane);
            }
            return Unique(
                    "Lot.ByCode",
                    nowCodes,
                    codes,
                    codeHolders,
                    false,
                    (key, detached) =>
                    {
                        var index = world.LotByCode();
                        var hit = (detached ? index.IncludeDetached() : index).TryGet(
                            key,
                            out var holder
                        );
                        return (hit, holder);
                    }
                )
                ?? Unique(
                    "Lot.ByCodeUnderParent",
                    nowParented,
                    parented,
                    parentedHolders,
                    true,
                    (key, detached) =>
                    {
                        var index = world.LotByCodeUnderParent();
                        var hit = (detached ? index.IncludeDetached() : index).TryGet(
                            key,
                            out var holder
                        );
                        return (hit, holder);
                    }
                )
                ?? Unique(
                    "SparseLot.ByCode",
                    nowSparse,
                    sparseCodes,
                    sparseHolders,
                    false,
                    (key, detached) =>
                    {
                        var index = world.SparseLotByCode();
                        var hit = (detached ? index.IncludeDetached() : index).TryGet(
                            key,
                            out var holder
                        );
                        return (hit, holder);
                    }
                )
                ?? Lists(lots)
                ?? Ordered(lots);
        }

        int Lane(Entity entity)
        {
            if (world.StateOf(entity) is LifecycleState.Dead or LifecycleState.PendingDestroy)
                return -1;
            return world.IsDetached(entity) ? 1 : 0;
        }

        static string? Unique<TKey>(
            string name,
            Dictionary<Entity, (TKey Key, int Lane)> now,
            Dictionary<Entity, (TKey Key, int Lane)> before,
            Dictionary<(TKey, int), Entity> holders,
            bool latestWins,
            Func<TKey, bool, (bool Hit, Entity Holder)> lookup
        )
            where TKey : notnull
        {
            var groups = new Dictionary<(TKey, int), List<Entity>>();
            foreach (var (entity, at) in now)
            {
                if (!groups.TryGetValue(at, out var members))
                    groups[at] = members = [];
                members.Add(entity);
            }
            var keys = new HashSet<TKey>();
            foreach (var (_, at) in now)
                keys.Add(at.Key);
            foreach (var (_, at) in before)
                keys.Add(at.Key);
            var elected = new Dictionary<(TKey, int), Entity>();
            foreach (var key in keys)
            {
                for (var lane = 0; lane < 2; lane++)
                {
                    groups.TryGetValue((key, lane), out var members);
                    if (lane == 1 && groups.ContainsKey((key, 0)))
                        continue;
                    var (hit, holder) = lookup(key, lane == 1);
                    if (members is null)
                    {
                        if (hit)
                            return $"{name} finds {holder} under {key}, which no entity holds";
                        continue;
                    }
                    if (!hit || !members.Contains(holder))
                        return $"{name} returns {(hit ? holder : "nothing")} for {key}, held by {string.Join(", ", members)}";
                    var arrived = members
                        .Where(e => !before.TryGetValue(e, out var was) || !was.Equals((key, lane)))
                        .ToList();
                    var stayed = members.Except(arrived).ToList();
                    holders.TryGetValue((key, lane), out var previous);
                    var allowed = latestWins
                        ? arrived.Count > 0
                            ? arrived
                            : stayed.Contains(previous)
                                ? [previous]
                                : members
                        : stayed.Contains(previous)
                            ? [previous]
                            : stayed.Count > 0
                                ? stayed
                                : arrived;
                    if (!allowed.Contains(holder))
                        return $"{name} gives {key} to {holder}; the policy allows {string.Join(", ", allowed)}";
                    elected[(key, lane)] = holder;
                }
            }
            before.Clear();
            foreach (var (entity, at) in now)
                before[entity] = at;
            holders.Clear();
            foreach (var (at, holder) in elected)
                holders[at] = holder;
            return null;
        }

        string? Lists(List<(Entity Entity, Lot Lot, int Lane)> lots)
        {
            for (var zone = 0; zone < Zones; zone++)
            {
                var z = zone;
                if (
                    Differs(
                        world.LotByZone().Get(zone),
                        lots.Where(l => l.Lane == 0 && l.Lot.Zone == z)
                    )
                    || Differs(
                        world.LotByZone().IncludeDetached().Get(zone),
                        lots.Where(l => l.Lot.Zone == z)
                    )
                )
                    return $"Lot.ByZone lists the wrong members of zone {zone}";
                for (var code = 0; code < Codes; code++)
                {
                    var c = code;
                    if (
                        Differs(
                            world.LotByZoneCode().Get((zone, code)),
                            lots.Where(l => l.Lane == 0 && l.Lot.Zone == z && l.Lot.Code == c)
                        )
                        || Differs(
                            world.LotByZoneCode().IncludeDetached().Get((zone, code)),
                            lots.Where(l => l.Lot.Zone == z && l.Lot.Code == c)
                        )
                    )
                        return $"Lot.ByZoneCode lists the wrong entities for ({zone}, {code})";
                }
            }
            return null;
        }

        static bool Differs(
            EntityList list,
            IEnumerable<(Entity Entity, Lot Lot, int Lane)> expected
        )
        {
            var actual = new Entity[list.Count];
            list.CopyTo(actual);
            var wanted = expected.Select(l => l.Entity).ToHashSet();
            return actual.Length != wanted.Count || !wanted.SetEquals(actual);
        }

        string? Ordered(List<(Entity Entity, Lot Lot, int Lane)> lots)
        {
            for (var detached = 0; detached < 2; detached++)
            {
                var index = detached == 1 ? world.LotDue().IncludeDetached() : world.LotDue();
                var listed = lots.Where(l => l.Lot.Due >= 0 && l.Lane <= detached).ToList();
                if (index.Count != listed.Count)
                    return $"Lot.Due counts {index.Count}, expected {listed.Count}";
                var peeked = index.TryPeek(out var first, out _);
                if (peeked != listed.Count > 0 || (peeked && first != listed.Min(l => l.Lot.Due)))
                    return "Lot.Due peeks the wrong entry";
                for (var bound = 0; bound < Dues; bound += 2)
                {
                    var due = new List<Entity>();
                    foreach (var entity in index.Due(bound))
                        due.Add(entity);
                    var keys = due.Select(e => world.Get<Lot>(e).Due).ToList();
                    var wanted = listed.Where(l => l.Lot.Due <= bound).Select(l => l.Entity);
                    if (!due.ToHashSet().SetEquals(wanted) || due.Count != wanted.Count())
                        return $"Lot.Due({bound}) yields the wrong entities";
                    if (!keys.SequenceEqual(keys.Order()))
                        return $"Lot.Due({bound}) is out of key order";
                }
            }
            return null;
        }
    }
}
