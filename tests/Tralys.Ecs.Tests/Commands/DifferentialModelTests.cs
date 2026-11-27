using System.Text;

namespace Tralys.Ecs.Tests;

public sealed class DifferentialModelTests
{
    const int Sequences = 1000;
    const int Seeded = 6;
    const int Length = 14;
    const int Relations = 6;

    enum Op
    {
        Create,
        Destroy,
        CancelDestroy,
        SetHealth,
        RemoveHealth,
        SetBurning,
        EnableBurning,
        DisableBurning,
        Detach,
        Reattach,
        SetParent,
        ClearParent,
        Relate,
        Unrelate,
        Retarget,
        ClearTargets,
        ClearSources,
        SetRow,
        RemoveRow,
        SetDialog,
        RemoveDialog,
        SetIdentity,
        SetClock,
        RemoveClock,
        SetPosition,
        RemovePosition,
        SetAnchored,
        SetBadge,
        RemoveBadge,
        EditHealth,
        ReplaceHealth,
        EditBadge,
        ReplaceBadge,
    }

    enum EdgeOp
    {
        Relate,
        Unrelate,
        Retarget,
        ClearTargets,
        ClearSources,
    }

    record struct Step(Op Op, int A, int B, int C, int Relation, int Value, bool Untracked);

    interface IWriter
    {
        World World { get; }
        List<char>? Results { get; }
        Entity Create();
        void Destroy(Entity entity);
        bool? CancelDestroy(Entity entity);
        void Set<T>(Entity entity, in T value)
            where T : struct, IComponent;
        bool? Remove<T>(Entity entity)
            where T : struct, IComponent;
        void SetEnabled<T>(Entity entity, bool enabled)
            where T : struct, IEnableable;
        void Replace<T>(Entity entity, in T value)
            where T : struct, IComponent;
        void Edit<T>(Entity entity, in T value)
            where T : struct, IMutableComponent;
        void Detach(Entity entity);
        void Reattach(Entity entity);
        void SetParent(Entity child, Entity parent);
        void ClearParent(Entity child);
        bool? Edge<R>(EdgeOp op, Entity source, Entity target, Entity newTarget)
            where R : struct, IRelation;
        void SetRow(Entity entity, int slot, int count);
        void RemoveRow(Entity entity, int slot);
    }

    sealed class WorldWriter(World world) : IWriter
    {
        public World World => world;

        public List<char>? Results { get; } = [];

        public Entity Create() => world.Create();

        public void Destroy(Entity entity) => world.Destroy(entity);

        public bool? CancelDestroy(Entity entity) => world.CancelDestroy(entity);

        public void Set<T>(Entity entity, in T value)
            where T : struct, IComponent => world.Set(entity, value);

        public bool? Remove<T>(Entity entity)
            where T : struct, IComponent => world.Remove<T>(entity);

        public void SetEnabled<T>(Entity entity, bool enabled)
            where T : struct, IEnableable => world.SetEnabled<T>(entity, enabled);

        public void Replace<T>(Entity entity, in T value)
            where T : struct, IComponent => world.Replace(entity, value);

        public void Edit<T>(Entity entity, in T value)
            where T : struct, IMutableComponent
        {
            using var edit = world.Edit<T>(entity);
            edit.Value = value;
        }

        public void Detach(Entity entity) => world.Detach(entity);

        public void Reattach(Entity entity) => world.Reattach(entity);

        public void SetParent(Entity child, Entity parent) => world.SetParent(child, parent);

        public void ClearParent(Entity child) => world.ClearParent(child);

        public bool? Edge<R>(EdgeOp op, Entity source, Entity target, Entity newTarget)
            where R : struct, IRelation
        {
            switch (op)
            {
                case EdgeOp.Relate:
                    world.Relate<R>(source, target);
                    break;
                case EdgeOp.Unrelate:
                    return world.Unrelate<R>(source, target);
                case EdgeOp.Retarget:
                    world.Retarget<R>(source, target, newTarget);
                    break;
                case EdgeOp.ClearTargets:
                    world.ClearTargets<R>(source);
                    break;
                case EdgeOp.ClearSources:
                    world.ClearSources<R>(source);
                    break;
            }
            return null;
        }

        public void SetRow(Entity entity, int slot, int count) =>
            world.TallyRows(entity).Set(new Tally { Slot = slot, Count = count });

        public void RemoveRow(Entity entity, int slot) => world.TallyRows(entity).Remove(slot);
    }

    sealed class CommandWriter(CommandBuffer buffer) : IWriter
    {
        public World World => buffer.world;

        public List<char>? Results => null;

        public Entity Create() => buffer.Commands.Create();

        public void Destroy(Entity entity) => buffer.Commands.Destroy(entity);

        public bool? CancelDestroy(Entity entity)
        {
            buffer.Commands.CancelDestroy(entity);
            return null;
        }

        public void Set<T>(Entity entity, in T value)
            where T : struct, IComponent => buffer.Commands.Set(entity, value);

        public bool? Remove<T>(Entity entity)
            where T : struct, IComponent
        {
            buffer.Commands.Remove<T>(entity);
            return null;
        }

        public void SetEnabled<T>(Entity entity, bool enabled)
            where T : struct, IEnableable => buffer.Commands.SetEnabled<T>(entity, enabled);

        // A parallel body reads presence as the world stood before its run, then writes through Set.
        public void Replace<T>(Entity entity, in T value)
            where T : struct, IComponent
        {
            if (entity.IsPlaceholder || !buffer.world.Reader.Has<T>(entity))
                throw new InvalidOperationException($"{entity} has no {typeof(T).Name}.");
            buffer.Commands.Set(entity, value);
        }

        public void Edit<T>(Entity entity, in T value)
            where T : struct, IMutableComponent => Replace(entity, value);

        public void Detach(Entity entity) => buffer.Commands.Detach(entity);

        public void Reattach(Entity entity) => buffer.Commands.Reattach(entity);

        public void SetParent(Entity child, Entity parent) =>
            buffer.Commands.SetParent(child, parent);

        public void ClearParent(Entity child) => buffer.Commands.ClearParent(child);

        public bool? Edge<R>(EdgeOp op, Entity source, Entity target, Entity newTarget)
            where R : struct, IRelation
        {
            switch (op)
            {
                case EdgeOp.Relate:
                    buffer.Commands.Relate<R>(source, target);
                    break;
                case EdgeOp.Unrelate:
                    buffer.Commands.Unrelate<R>(source, target);
                    break;
                case EdgeOp.Retarget:
                    buffer.Commands.Retarget<R>(source, target, newTarget);
                    break;
                case EdgeOp.ClearTargets:
                    buffer.Commands.ClearTargets<R>(source);
                    break;
                case EdgeOp.ClearSources:
                    buffer.Commands.ClearSources<R>(source);
                    break;
            }
            return null;
        }

        public void SetRow(Entity entity, int slot, int count) =>
            buffer.Commands.Rows<int, Tally>(entity).Set(new Tally { Slot = slot, Count = count });

        public void RemoveRow(Entity entity, int slot) =>
            buffer.Commands.Rows<int, Tally>(entity).Remove(slot);
    }

    static bool? Edge(IWriter writer, int relation, EdgeOp op, Entity a, Entity b, Entity c) =>
        relation switch
        {
            0 => writer.Edge<Follows>(op, a, b, c),
            1 => writer.Edge<Escorts>(op, a, b, c),
            2 => writer.Edge<CrewOf>(op, a, b, c),
            3 => writer.Edge<AlliedWith>(op, a, b, c),
            4 => writer.Edge<Bonded>(op, a, b, c),
            _ => writer.Edge<Sworn>(op, a, b, c),
        };

    static bool? Apply(IWriter writer, in Step step, List<Entity> handles)
    {
        var a = handles[step.A];
        switch (step.Op)
        {
            case Op.Create:
                handles.Add(writer.Create());
                return null;
            case Op.Destroy:
                writer.Destroy(a);
                return null;
            case Op.CancelDestroy:
                return writer.CancelDestroy(a);
            case Op.SetHealth:
                writer.Set(a, new Health { Value = step.Value });
                return null;
            case Op.RemoveHealth:
                return writer.Remove<Health>(a);
            case Op.SetBurning:
                writer.Set(a, new Burning { Ticks = step.Value });
                return null;
            case Op.EnableBurning:
                writer.SetEnabled<Burning>(a, true);
                return null;
            case Op.DisableBurning:
                writer.SetEnabled<Burning>(a, false);
                return null;
            case Op.Detach:
                writer.Detach(a);
                return null;
            case Op.Reattach:
                writer.Reattach(a);
                return null;
            case Op.SetParent:
                writer.SetParent(a, handles[step.B]);
                return null;
            case Op.ClearParent:
                writer.ClearParent(a);
                return null;
            case Op.Relate:
                return Edge(writer, step.Relation, EdgeOp.Relate, a, handles[step.B], default);
            case Op.Unrelate:
                return Edge(writer, step.Relation, EdgeOp.Unrelate, a, handles[step.B], default);
            case Op.Retarget:
                return Edge(
                    writer,
                    step.Relation,
                    EdgeOp.Retarget,
                    a,
                    handles[step.B],
                    handles[step.C]
                );
            case Op.ClearTargets:
                return Edge(writer, step.Relation, EdgeOp.ClearTargets, a, default, default);
            case Op.ClearSources:
                return Edge(writer, step.Relation, EdgeOp.ClearSources, a, default, default);
            case Op.SetRow:
                writer.SetRow(a, step.Value & 1, step.Value);
                return null;
            case Op.RemoveRow:
                writer.RemoveRow(a, step.Value & 1);
                return null;
            case Op.SetDialog:
                writer.Set(a, new Dialog { Page = step.Value });
                return null;
            case Op.RemoveDialog:
                return writer.Remove<Dialog>(a);
            case Op.SetIdentity:
                writer.Set(a, new Identity { Id = step.Value });
                return null;
            case Op.SetClock:
                writer.Set(a, new Clock { Tick = step.Value });
                return null;
            case Op.RemoveClock:
                return writer.Remove<Clock>(a);
            case Op.SetPosition:
                writer.Set(a, new Position { X = step.Value });
                return null;
            case Op.RemovePosition:
                return writer.Remove<Position>(a);
            case Op.SetAnchored:
                writer.Set(a, new Anchored { Length = step.Value });
                return null;
            case Op.SetBadge:
                writer.Set(a, new Badge { Number = step.Value });
                return null;
            case Op.RemoveBadge:
                return writer.Remove<Badge>(a);
            case Op.EditHealth:
                writer.Edit(a, new Health { Value = step.Value });
                return null;
            case Op.ReplaceHealth:
                writer.Replace(a, new Health { Value = step.Value });
                return null;
            case Op.EditBadge:
                writer.Edit(a, new Badge { Number = step.Value });
                return null;
            case Op.ReplaceBadge:
                writer.Replace(a, new Badge { Number = step.Value });
                return null;
        }
        return null;
    }

    static bool? Run(IWriter writer, in Step step, List<Entity> handles)
    {
        if (!step.Untracked)
            return Apply(writer, step, handles);
        using var untracked = writer.World.Untracked();
        return Apply(writer, step, handles);
    }

    static bool Broken(Exception error) =>
        error is BrokenInvariantException
        || (error is AggregateException aggregate && aggregate.InnerExceptions.Any(Broken));

    static bool Rejected(Exception error) =>
        error is InvalidOperationException or ArgumentException or AggregateException
        && !Broken(error);

    static bool Policed(Exception error) =>
        error is TargetPolicyException
        || (error is AggregateException aggregate && aggregate.InnerExceptions.Any(Policed));

    sealed class Injected() : Exception("A scripted body failed on purpose.");

    static bool Carries(Exception? error) =>
        error is Injected
        || (error is AggregateException aggregate && aggregate.InnerExceptions.Any(Carries));

    // A recorded destroy is judged once its batch lands, so after a step a target policy turned away the paths may part.
    static bool Attempt(IWriter writer, in Step step, List<Entity> handles)
    {
        try
        {
            var result = Run(writer, step, handles);
            writer.Results?.Add(
                result switch
                {
                    true => 'T',
                    false => 'F',
                    null => '.',
                }
            );
            return false;
        }
        catch (Exception error) when (Rejected(error))
        {
            if (step.Op == Op.Create)
                throw;
            writer.Results?.Add('!');
            return Policed(error);
        }
    }

    static bool Attempt(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception error) when (Rejected(error))
        {
            return Policed(error);
        }
    }

    static (List<Entity> Handles, Entity Trigger) Setup(World world, int seed)
    {
        var random = new Random(seed);
        var writer = new WorldWriter(world);
        var handles = new List<Entity>();
        for (var i = 0; i < Seeded; i++)
            handles.Add(world.Create());
        for (var i = 1; i < Seeded; i++)
        {
            if (random.Next(3) == 0)
                world.SetParent(handles[i], handles[random.Next(i)]);
        }
        foreach (var entity in handles)
        {
            if (random.Next(2) == 0)
                world.Set(entity, new Health { Value = random.Next(1, 4) });
            if (random.Next(3) == 0)
            {
                world.Set(entity, new Burning { Ticks = 1 });
                if (random.Next(2) == 0)
                    world.SetEnabled<Burning>(entity, false);
            }
            if (random.Next(3) == 0)
                world.TallyRows(entity).Set(new Tally { Slot = random.Next(2), Count = 1 });
            if (random.Next(3) == 0)
                world.Set(entity, new Position { X = 1 });
            if (random.Next(4) == 0)
                world.Set(entity, new Dialog { Page = random.Next(1, 4) });
            if (random.Next(4) == 0)
                world.Set(entity, new Badge { Number = random.Next(1, 4) });
            if (random.Next(4) == 0)
                world.Set(entity, new Identity { Id = random.Next(1, 4) });
            if (random.Next(5) == 0)
                world.Detach(entity);
        }
        if (random.Next(2) == 0)
            world.Set(handles[random.Next(Seeded)], new Clock { Tick = 1 });
        for (var k = 0; k < 5; k++)
        {
            var a = random.Next(Seeded);
            var b = (a + 1 + random.Next(Seeded - 1)) % Seeded;
            var relation = random.Next(Relations);
            Attempt(() => Edge(writer, relation, EdgeOp.Relate, handles[a], handles[b], default));
        }
        for (var k = 0; k < 2; k++)
        {
            var doomed = handles[random.Next(Seeded)];
            if (random.Next(2) == 0)
                Attempt(() => world.Destroy(doomed));
        }
        return (handles, world.Create());
    }

    static Step Next(Random random, World world, List<Entity> handles)
    {
        var count = handles.Count;
        var op = (Op)random.Next(Enum.GetValues<Op>().Length);
        var a = random.Next(count);
        var b = (a + 1 + random.Next(count - 1)) % count;
        var c = random.Next(count);
        var relation = random.Next(Relations);
        if (op == Op.Retarget && random.Next(4) != 0)
        {
            var targets = TargetsOf(world, relation, handles[a]);
            if (targets.Count > 0)
                b = handles.IndexOf(targets[random.Next(targets.Count)]);
            if (b < 0)
                b = (a + 1) % count;
        }
        return new Step(op, a, b, c, relation, random.Next(1, 4), random.Next(6) == 0);
    }

    static List<Entity> TargetsOf(World world, int relation, Entity source) =>
        relation switch
        {
            0 => Collect<Follows>(world, source),
            1 => Collect<Escorts>(world, source),
            2 => Collect<CrewOf>(world, source),
            3 => Collect<AlliedWith>(world, source),
            4 => Collect<Bonded>(world, source),
            _ => Collect<Sworn>(world, source),
        };

    static List<Entity> Collect<R>(World world, Entity source)
        where R : struct, IRelation
    {
        var targets = new List<Entity>();
        foreach (var target in world.Targets<R>(source))
            targets.Add(target);
        return targets;
    }

    sealed class Runner(Step[] steps, int count, List<Entity> handles, IWriter writer, bool throws)
        : IAddObserver<Scratchpad>
    {
        public bool Policed;

        public void OnAdd(in ObserverContext context, Entity entity, in Scratchpad value)
        {
            for (var i = 0; i < count; i++)
                Policed |= Attempt(writer, steps[i], handles);
            if (throws)
                throw new Injected();
        }
    }

    sealed class Finaliser(
        Entity trigger,
        Step[] steps,
        int count,
        List<Entity> handles,
        IWriter writer,
        bool throws
    ) : IDestroyObserver
    {
        public bool Policed;

        public void OnDestroy(in ObserverContext context, Entity entity)
        {
            if (entity != trigger)
                return;
            for (var i = 0; i < count; i++)
                Policed |= Attempt(writer, steps[i], handles);
            if (throws)
                throw new Injected();
        }
    }

    struct Scripted(Step[] steps, int count, List<Entity> handles, WorldWriter writer, Flag policed)
        : IChunkKernel
    {
        public readonly void Execute(ref ChunkView chunk)
        {
            foreach (var _ in chunk.Rows)
            {
                for (var i = 0; i < count; i++)
                    policed.Set |= Attempt(writer, steps[i], handles);
                throw new Injected();
            }
        }
    }

    sealed record Outcome(
        string Fingerprint,
        string Shape,
        string Stamps,
        HashSet<string> Changed,
        string? Results,
        CaptureImage Capture,
        Dictionary<int, List<bool>> Partitions,
        bool[] Start,
        bool?[] Detached,
        string? Replay,
        bool Policed,
        bool Leftover,
        Exception? First
    )
    {
        public bool Lost { get; init; }
    }

    static Exception? Play(World world)
    {
        try
        {
            world.Playback();
            return null;
        }
        catch (Exception error) when (!Broken(error))
        {
            return error;
        }
    }

    sealed class Watch(World world, List<Entity> handles, LogTail tail)
    {
        public readonly LogTail Tail = tail;
        public readonly Dictionary<string, string> Start = Values(world, handles);
        public readonly CursorId Cursor = world.Changes.Register("oracle");
    }

    static Dictionary<string, string> Values(World world, List<Entity> handles)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < handles.Count; i++)
        {
            var entity = handles[i];
            if (!world.Exists(entity))
                continue;
            if (world.TryGet<Health>(entity, out var health))
                values[$"hp{i}"] = health.Value.ToString();
            if (world.TryGet<Burning>(entity, out var burning))
                values[$"burn{i}"] = burning.Ticks.ToString();
            if (world.TryGet<Dialog>(entity, out var dialog))
                values[$"dialog{i}"] = dialog.Page.ToString();
            if (world.TryGet<Position>(entity, out var position))
                values[$"pos{i}"] = position.X.ToString();
            if (world.TryGet<Badge>(entity, out var badge))
                values[$"badge{i}"] = badge.Number.ToString();
        }
        return values;
    }

    static Outcome Finish(
        World world,
        List<Entity> handles,
        Watch watch,
        bool[] start,
        IWriter? writer,
        Func<bool> policed
    )
    {
        var first = Play(world);
        var leftover =
            world.PlaybackOwed
            || handles.Any(e => world.StateOf(e) == LifecycleState.PendingDestroy);
        var replay = Play(world)?.GetType().Name;
        var ids = new Dictionary<Entity, int>();
        for (var i = 0; i < handles.Count; i++)
            ids[handles[i]] = i;
        var partitions = new Dictionary<int, List<bool>>();
        foreach (var entry in watch.Tail.Read())
        {
            if (
                entry.Kind is not (LogKind.Detached or LogKind.Reattached)
                || !ids.TryGetValue(entry.Entity, out var id)
            )
                continue;
            if (!partitions.TryGetValue(id, out var seen))
                partitions[id] = seen = [];
            seen.Add(entry.Kind == LogKind.Detached);
        }
        var alive = handles.Where(e => world.StateOf(e) == LifecycleState.Alive).ToArray();
        var visible = handles.Select(e => alive.Contains(e) && !world.IsDetached(e) ? e : default);
        var changed = Values(world, [.. visible])
            .Where(end => watch.Start.GetValueOrDefault(end.Key) != end.Value)
            .Select(end => end.Key)
            .ToHashSet();
        return new Outcome(
            Fingerprint(world, handles, ids, ordered: true),
            Fingerprint(world, handles, ids, ordered: false),
            Stamps(world, watch.Cursor, ids),
            changed,
            writer?.Results is { } results ? new string([.. results]) : null,
            world.Capture(alive),
            partitions,
            start,
            [.. handles.Select(e => world.Exists(e) ? world.IsDetached(e) : (bool?)null)],
            replay,
            policed() || (first is not null && Policed(first)),
            leftover,
            first
        );
    }

    static string Stamps(World world, CursorId cursor, Dictionary<Entity, int> ids)
    {
        var stamps = new List<string>();
        void Note(string type, Entity entity, bool added)
        {
            if (ids.TryGetValue(entity, out var id))
                stamps.Add(added ? $"{type}{id}+" : $"{type}{id}");
        }
        var sample = world.Changes.Sample(cursor);
        foreach (var row in sample.Changed<Health>())
            Note("hp", row.Entity, row.Added);
        foreach (var row in sample.Changed<Burning>())
            Note("burn", row.Entity, row.Added);
        foreach (var row in sample.Changed<Dialog>())
            Note("dialog", row.Entity, row.Added);
        foreach (var row in sample.Changed<Position>())
            Note("pos", row.Entity, row.Added);
        foreach (var row in sample.Changed<Badge>())
            Note("badge", row.Entity, row.Added);
        stamps.Sort(StringComparer.Ordinal);
        return string.Join(" ", stamps);
    }

    static string Fingerprint(
        World world,
        List<Entity> handles,
        Dictionary<Entity, int> ids,
        bool ordered
    )
    {
        string Id(Entity entity) =>
            entity.IsDefault ? "-"
            : ids.TryGetValue(entity, out var id) ? id.ToString()
            : "?";
        IEnumerable<string> Ordered(IEnumerable<string> items) =>
            ordered ? items : items.Order(StringComparer.Ordinal);
        var text = new StringBuilder();
        for (var i = 0; i < handles.Count; i++)
        {
            var entity = handles[i];
            text.Append(i).Append(": ").Append(world.StateOf(entity));
            if (world.Exists(entity))
            {
                if (world.TryGet<Health>(entity, out var health))
                    text.Append(" hp").Append(health.Value);
                if (world.TryGetStored<Burning>(entity, out var burning))
                    text.Append(" burn")
                        .Append(burning.Ticks)
                        .Append(world.Has<Burning>(entity) ? '+' : '-');
                if (world.TryGet<Dialog>(entity, out var dialog))
                    text.Append(" dialog").Append(dialog.Page);
                if (world.TryGet<Identity>(entity, out var identity))
                    text.Append(" id").Append(identity.Id);
                if (world.Has<Clock>(entity))
                    text.Append(" clock");
                if (world.TryGet<Position>(entity, out var position))
                    text.Append(" pos").Append(position.X);
                if (world.TryGet<Anchored>(entity, out var anchored))
                    text.Append(" anchored").Append(anchored.Length);
                if (world.TryGet<Badge>(entity, out var badge))
                    text.Append(" badge").Append(badge.Number);
                if (world.IsDetached(entity))
                    text.Append(" detached");
                text.Append(" parent ").Append(Id(world.Parent(entity)));
                var children = new List<string>();
                foreach (var child in world.Children(entity))
                    children.Add(" child " + Id(child));
                text.AppendJoin("", Ordered(children));
                for (var relation = 0; relation < Relations; relation++)
                    text.AppendJoin(
                        "",
                        Ordered(
                            TargetsOf(world, relation, entity).Select(t => $" r{relation}>{Id(t)}")
                        )
                    );
                var rows = new List<string>();
                foreach (var row in world.TallyRows(entity))
                    rows.Add($" row{row.Slot}={row.Count}");
                text.AppendJoin("", Ordered(rows));
            }
            text.AppendLine();
        }
        text.Append("badges");
        for (var number = 1; number < 4; number++)
            text.Append(' ')
                .Append(world.BadgeByNumber().TryGet(number, out var holder) ? Id(holder) : "none");
        text.AppendLine();
        return text.ToString();
    }

    static Outcome Immediate(int seed, Random random, out Step[] steps, out List<Entity> created)
    {
        using var world = TestWorlds.Logged();
        var (handles, _) = Setup(world, seed);
        var watch = new Watch(world, handles, new LogTail(world));
        bool[] start = [.. handles.Select(world.IsDetached)];
        var writer = new WorldWriter(world);
        var script = new List<Step>();
        var policed = false;
        for (var i = 0; i < Length; i++)
        {
            var step = Next(random, world, handles);
            script.Add(step);
            policed |= Attempt(writer, step, handles);
        }
        steps = [.. script];
        created = handles;
        return Finish(world, handles, watch, start, writer, () => policed);
    }

    static Outcome Replayed(int seed, Step[] steps, int count, bool settled)
    {
        using var world = TestWorlds.Logged();
        var (handles, _) = Setup(world, seed);
        if (settled)
            Play(world);
        var watch = new Watch(world, handles, new LogTail(world));
        bool[] start = [.. handles.Select(world.IsDetached)];
        var writer = new WorldWriter(world);
        var policed = false;
        for (var i = 0; i < count; i++)
            policed |= Attempt(writer, steps[i], handles);
        return Finish(world, handles, watch, start, writer, () => policed);
    }

    static Outcome Recorded(int seed, Step[] steps, List<Entity> real, HashSet<CommandKind> kinds)
    {
        using var world = TestWorlds.Logged();
        var (handles, _) = Setup(world, seed);
        var watch = new Watch(world, handles, new LogTail(world));
        bool[] start = [.. handles.Select(world.IsDetached)];
        var buffer = world.RentBuffer(world.runSequence, 0);
        var writer = new CommandWriter(buffer);
        foreach (var step in steps)
            Attempt(writer, step, handles);
        for (var i = 0; i < buffer.count; i++)
            kinds.Add(buffer.records[i].Kind);
        world.Submit(buffer);
        return Finish(world, real, watch, start, writer, () => false);
    }

    sealed class Flag
    {
        public bool Set;
    }

    static void InBatches(World world, List<Entity> handles, int next, Action script)
    {
        if (next == handles.Count)
        {
            script();
            return;
        }
        using (world.Batch(handles[next]))
            InBatches(world, handles, next + 1, script);
    }

    static void Batched(
        World world,
        Step[] steps,
        int from,
        List<Entity> handles,
        WorldWriter writer,
        Flag policed
    )
    {
        for (var i = from; i < steps.Length; i++)
        {
            policed.Set |= Attempt(writer, steps[i], handles);
            if (steps[i].Op != Op.Create)
                continue;
            using (world.Batch(handles[^1]))
                Batched(world, steps, i + 1, handles, writer, policed);
            return;
        }
    }

    static Outcome Batched(int seed, Step[] steps)
    {
        using var world = TestWorlds.Logged();
        var (handles, _) = Setup(world, seed);
        var watch = new Watch(world, handles, new LogTail(world));
        bool[] start = [.. handles.Select(world.IsDetached)];
        var writer = new WorldWriter(world);
        var policed = new Flag();
        var refused = Attempt(() =>
            InBatches(world, handles, 0, () => Batched(world, steps, 0, handles, writer, policed))
        );
        return Finish(world, handles, watch, start, writer, () => policed.Set || refused);
    }

    static readonly QueryDesc Scratched = QueryDesc.Builder().Read<Scratchpad>().Build();

    static Outcome Walked(int seed, Step[] steps)
    {
        using var world = TestWorlds.Logged();
        var (handles, trigger) = Setup(world, seed);
        world.Set(trigger, new Scratchpad());
        var watch = new Watch(world, handles, new LogTail(world));
        bool[] start = [.. handles.Select(world.IsDetached)];
        var writer = new WorldWriter(world);
        var policed = new Flag();
        var refused = Attempt(() =>
        {
            foreach (var chunk in world.Query(Scratched))
            foreach (var _ in chunk.Rows)
            foreach (var step in steps)
                policed.Set |= Attempt(writer, step, handles);
        });
        return Finish(world, handles, watch, start, writer, () => policed.Set || refused);
    }

    static Outcome WalkedThrowing(int seed, Step[] steps, int count)
    {
        using var world = TestWorlds.Logged();
        var (handles, trigger) = Setup(world, seed);
        world.Set(trigger, new Scratchpad());
        var watch = new Watch(world, handles, new LogTail(world));
        bool[] start = [.. handles.Select(world.IsDetached)];
        var policed = new Flag();
        var writer = new WorldWriter(world);
        var kernel = new Scripted(steps, count, handles, writer, policed);
        Exception? thrown = null;
        try
        {
            world.Query(Scratched).Run(ref kernel);
        }
        catch (Exception error)
        {
            thrown = error;
        }
        var outcome = Finish(
            world,
            handles,
            watch,
            start,
            writer,
            () => policed.Set || (thrown is not null && Policed(thrown))
        );
        return outcome with { Lost = !Carries(thrown) };
    }

    static Outcome Observed(int seed, Step[] steps, int count, bool throws)
    {
        using var world = TestWorlds.Logged();
        var (handles, trigger) = Setup(world, seed);
        var watch = new Watch(world, handles, new LogTail(world));
        bool[] start = [.. handles.Select(world.IsDetached)];
        var writer = new WorldWriter(world);
        var runner = new Runner(steps, count, handles, writer, throws);
        world.Observers.OnAdd(runner);
        world.Set(trigger, new Scratchpad());
        var outcome = Finish(world, handles, watch, start, writer, () => runner.Policed);
        return outcome with { Lost = throws && !Carries(outcome.First) };
    }

    static Outcome Finalised(int seed, Step[] steps, int count, bool throws)
    {
        using var world = TestWorlds.Logged();
        var (handles, trigger) = Setup(world, seed);
        Play(world);
        var watch = new Watch(world, handles, new LogTail(world));
        bool[] start = [.. handles.Select(world.IsDetached)];
        var writer = new WorldWriter(world);
        var finaliser = new Finaliser(trigger, steps, count, handles, writer, throws);
        world.Observers.OnDestroy(finaliser);
        world.Destroy(trigger);
        var outcome = Finish(world, handles, watch, start, writer, () => finaliser.Policed);
        return outcome with { Lost = throws && !Carries(outcome.First) };
    }

    static Op[] PrefixOps { get; } =
    [
        .. Enum.GetValues<Op>()
            .Where(op =>
                op is not (Op.Create or Op.SetParent or Op.ClearParent or Op.CancelDestroy)
            ),
    ];

    static Outcome AfterParallel(int seed, Step[] steps, bool walk)
    {
        using var world = TestWorlds.Logged();
        var (handles, trigger) = Setup(world, seed);
        world.Set(trigger, new Scratchpad());
        var watch = new Watch(world, handles, new LogTail(world));
        bool[] start = [.. handles.Select(world.IsDetached)];
        var buffer = world.RentBuffer(world.runSequence, 0);
        var commands = new CommandWriter(buffer);
        var random = new Random(seed * 13);
        for (var i = 0; i < 4; i++)
        {
            var step = Next(random, world, handles) with
            {
                Op = PrefixOps[random.Next(PrefixOps.Length)],
            };
            Attempt(commands, step, handles);
        }
        world.Submit(buffer);
        world.runSequence++;
        var writer = new WorldWriter(world);
        var policed = new Flag();
        var refused = false;
        if (walk)
            refused = Attempt(() =>
            {
                foreach (var chunk in world.Query(Scratched))
                foreach (var _ in chunk.Rows)
                foreach (var step in steps)
                    policed.Set |= Attempt(writer, step, handles);
            });
        else
            foreach (var step in steps)
                policed.Set |= Attempt(writer, step, handles);
        return Finish(world, handles, watch, start, writer, () => policed.Set || refused);
    }

    // A playback applies a batch's partition flags before its reparents, so only a batch without reparents logs each move once.
    static bool Reparents(Step step) =>
        step.Op is Op.SetParent or Op.ClearParent or Op.CancelDestroy;

    // A playback stamps each component's net change over its batch, so it may stamp less than the writes one by one did, and mark an add
    // they saw as a change, but never stamp what they left alone, nor miss a net change they stamped.
    static string? Unstamped(Outcome expected, Outcome actual)
    {
        static HashSet<string> Keys(string stamps) =>
            [
                .. stamps
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.TrimEnd('+')),
            ];
        var stamped = Keys(actual.Stamps);
        var reference = Keys(expected.Stamps);
        if (stamped.Except(reference).FirstOrDefault() is { } extra)
            return $"{extra} is stamped";
        if (reference.Where(actual.Changed.Contains).Except(stamped).FirstOrDefault() is { } missed)
            return $"{missed} is not stamped";
        return null;
    }

    static string? Compare(
        string path,
        bool equal,
        bool strict,
        bool netted,
        Outcome expected,
        Outcome actual
    )
    {
        if (actual.Lost)
            return $"{path}: the body's own exception did not surface";
        if (actual.Leftover)
            return $"{path}: a finalising playback left work owed";
        if (actual.Replay is { } replay)
            return $"{path}: a second finalising playback threw {replay}";
        if (!equal || expected.Policed || actual.Policed)
            return Consistent(path, false, expected, actual);
        if (
            (strict ? actual.Fingerprint : actual.Shape)
            != (strict ? expected.Fingerprint : expected.Shape)
        )
            return $"{path} ends in another state:\n{actual.Fingerprint}expected:\n{expected.Fingerprint}";
        if (strict && Unstamped(expected, actual) is { } stamp)
            return $"{path} stamps [{actual.Stamps}] ({stamp}), the immediate writes [{expected.Stamps}]";
        if (Answers(actual.Results, expected.Results) is { } step)
            return $"{path} returns {actual.Results} at step {step}, the immediate writes {expected.Results}";
        var differences = CaptureImage
            .Compare(expected.Capture, actual.Capture)
            .Where(d =>
                strict
                || d.Kind
                    is not (
                        DifferenceKind.ChildOrderDiffers
                        or DifferenceKind.RelationsDiffer
                        or DifferenceKind.RowsDiffer
                    )
            )
            .ToList();
        if (differences.Count > 0)
            return $"{path} captures differently: {string.Join("; ", differences.Take(4))}";
        return Consistent(path, netted, expected, actual);
    }

    // Only the answers both paths gave: a write that waits is checked when it applies, so where one threw the other may not have.
    static int? Answers(string? actual, string? expected)
    {
        if (actual is null || expected is null)
            return null;
        for (var i = 0; i < Math.Min(actual.Length, expected.Length); i++)
        {
            if (actual[i] is 'T' or 'F' && expected[i] is 'T' or 'F' && actual[i] != expected[i])
                return i;
        }
        return null;
    }

    static bool Claims(Step step) => step.Op is Op.SetClock or Op.SetAnchored;

    // A parallel buffer's singleton claims and requirements are judged against its whole batch at playback, not in call order.
    // The commands path reads presence before any of its steps, so an edit after a step that set or removed the type has no equivalent.
    static bool ReadsAhead(Step[] steps)
    {
        for (var i = 0; i < steps.Length; i++)
        {
            if (TypeOfEdit(steps[i].Op) is not { } edited)
                continue;
            for (var j = 0; j < i; j++)
            {
                if (steps[j].A == steps[i].A && TypeOfWrite(steps[j].Op) == edited)
                    return true;
            }
        }
        return false;

        static Type? TypeOfEdit(Op op) =>
            op switch
            {
                Op.EditHealth or Op.ReplaceHealth => typeof(Health),
                Op.EditBadge or Op.ReplaceBadge => typeof(Badge),
                _ => null,
            };

        static Type? TypeOfWrite(Op op) =>
            op switch
            {
                Op.SetHealth or Op.RemoveHealth => typeof(Health),
                Op.SetBadge or Op.RemoveBadge => typeof(Badge),
                _ => null,
            };
    }

    static bool JudgedAsBatch(Step[] steps, Outcome expected, Outcome recorded) =>
        steps.Any(Claims)
        && (
            recorded.First is CommandRejectedException
            || (
                recorded.First is AggregateException aggregate
                && aggregate.InnerExceptions.Any(e => e is CommandRejectedException)
            )
            || steps.Where((step, i) => Claims(step) && expected.Results?[i] == '!').Any()
        );

    static string? Consistent(string path, bool netted, Outcome expected, Outcome actual)
    {
        foreach (var (id, entries) in actual.Partitions)
        {
            if (actual.Detached[id] is not { } end)
                continue;
            var heard = expected.Partitions.GetValueOrDefault(id)?.Count ?? 0;
            var at = id < actual.Start.Length && actual.Start[id];
            var consistent = !netted || entries.Count <= heard;
            foreach (var detached in entries)
            {
                consistent &= detached != at;
                at = detached;
            }
            if (!consistent || at != end)
                return $"{path} logs partition moves [{string.Join(",", entries.Select(d => d ? "Detached" : "Reattached"))}] for {id}; the immediate writes logged {heard}";
        }
        return null;
    }

    [Test]
    public async Task Every_write_path_ends_where_the_immediate_writes_do()
    {
        var kinds = new HashSet<CommandKind>();
        var mismatches = new List<string>();
        for (var run = 0; run < Sequences && mismatches.Count < 3; run++)
        {
            var seed = 1000 + run;
            Step[] steps = [];
            try
            {
                var expected = Immediate(seed, new Random(seed * 7), out steps, out var real);
                if (expected.Replay is { } replay)
                    mismatches.Add($"run {run}: a second immediate playback threw {replay}");
                if (expected.Leftover)
                    mismatches.Add($"run {run}: the immediate writes' playback left work owed");
                var cut = new Random(seed * 11).Next(steps.Length + 1);
                var netted = !steps.Any(Reparents);
                var nettedCut = !steps.Take(cut).Any(Reparents);
                var replayed = Replayed(seed, steps, cut, settled: false);
                // An observer's destroys, cancels, reparents and edges apply at once beside its recorded writes: no order across entities.
                foreach (
                    var (path, equal, strict, nets, reference, outcome) in new (
                        string,
                        bool,
                        bool,
                        bool,
                        Func<Outcome>,
                        Func<Outcome>
                    )[]
                    {
                        (
                            "Commands",
                            true,
                            true,
                            netted,
                            () => expected,
                            () => Recorded(seed, steps, real, kinds)
                        ),
                        ("Batch", true, false, false, () => expected, () => Batched(seed, steps)),
                        ("Walk", true, true, netted, () => expected, () => Walked(seed, steps)),
                        (
                            "Observer",
                            false,
                            false,
                            false,
                            () => expected,
                            () => Observed(seed, steps, steps.Length, throws: false)
                        ),
                        (
                            "Finalisation",
                            true,
                            true,
                            netted,
                            () => Replayed(seed, steps, steps.Length, settled: true),
                            () => Finalised(seed, steps, steps.Length, throws: false)
                        ),
                        (
                            "Walk after parallel commands",
                            true,
                            true,
                            netted,
                            () => AfterParallel(seed, steps, walk: false),
                            () => AfterParallel(seed, steps, walk: true)
                        ),
                        (
                            "Walk that throws",
                            true,
                            true,
                            nettedCut,
                            () => replayed,
                            () => WalkedThrowing(seed, steps, cut)
                        ),
                        (
                            "Observer that throws",
                            false,
                            false,
                            false,
                            () => replayed,
                            () => Observed(seed, steps, cut, throws: true)
                        ),
                        (
                            "Finalisation that throws",
                            true,
                            true,
                            nettedCut,
                            () => Replayed(seed, steps, cut, settled: true),
                            () => Finalised(seed, steps, cut, throws: true)
                        ),
                    }
                )
                {
                    var (before, after) = (reference(), outcome());
                    var comparable =
                        equal
                        && !(
                            path == "Commands"
                            && (JudgedAsBatch(steps, before, after) || ReadsAhead(steps))
                        );
                    if (Compare(path, comparable, strict, nets, before, after) is { } mismatch)
                    {
                        mismatches.Add(
                            $"run {run}, steps {string.Join(" ", steps.Select(s => $"{s.Op}({s.A},{s.B},{s.C},r{s.Relation},{s.Value}{(s.Untracked ? ",u" : "")})"))}: {mismatch}"
                        );
                        break;
                    }
                }
            }
            catch (Exception error)
            {
                mismatches.Add(
                    $"run {run}, steps {string.Join(" ", steps.Select(s => $"{s.Op}({s.A},{s.B},{s.C},r{s.Relation},{s.Value}{(s.Untracked ? ",u" : "")})"))}: {error}"
                );
            }
        }

        await Assert.That(mismatches).IsEmpty();
        await Assert.That(kinds).IsEquivalentTo(Enum.GetValues<CommandKind>());
    }
}
