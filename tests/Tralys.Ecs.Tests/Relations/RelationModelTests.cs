namespace Tralys.Ecs.Tests;

public sealed class RelationModelTests
{
    [Test]
    public async Task Random_edits_match_a_reference_model_and_the_log_replays_to_the_same_state()
    {
        await Assert.That(Run(seed: 7, steps: 3000)).IsNull();
    }

    static string? Run(int seed, int steps)
    {
        using var world = TestWorlds.Create(new WorldOptions { AttributedLogCapacity = 1 << 16 });
        var tail = new LogTail(world);
        var random = new Random(seed);
        var model = new Model();
        var live = new List<Entity>();
        for (var i = 0; i < 16; i++)
            live.Add(world.Create());

        for (var step = 0; step < steps; step++)
        {
            var a = live[random.Next(live.Count)];
            var b = live[random.Next(live.Count)];
            switch (random.Next(8))
            {
                case 0:
                    world.Relate(a, b, new Follows());
                    model.Relate(model.Follows, a, b, exclusive: false, symmetric: false);
                    break;
                case 1:
                    world.Unrelate<Follows>(a, b);
                    model.Unrelate(model.Follows, a, b, symmetric: false);
                    break;
                case 2:
                    world.Relate(a, b, new OwnedBy());
                    model.Relate(model.OwnedBy, a, b, exclusive: true, symmetric: false);
                    break;
                case 3:
                    world.Relate(a, b, new AlliedWith());
                    model.Relate(model.Allied, a, b, exclusive: false, symmetric: true);
                    break;
                case 4:
                    world.Unrelate<AlliedWith>(a, b);
                    model.Unrelate(model.Allied, a, b, symmetric: true);
                    break;
                case 5:
                    if (model.Follows.Targets(a) is not { Count: > 0 } targets)
                        break;
                    var old = targets[random.Next(targets.Count)];
                    world.Retarget<Follows>(a, old, b);
                    model.Unrelate(model.Follows, a, old, symmetric: false);
                    model.Relate(model.Follows, a, b, exclusive: false, symmetric: false);
                    break;
                case 6:
                    var parent = random.Next(4) == 0 ? default(Entity) : b;
                    if (model.WouldCycle(a, parent))
                        break;
                    var index = random.Next(-1, 3);
                    if (parent.IsDefault)
                        world.ClearParent(a);
                    else
                        world.SetParent(a, parent, index);
                    model.SetParent(a, parent, index);
                    break;
                case 7:
                    if (random.Next(3) != 0)
                        break;
                    world.Destroy(a);
                    world.Playback();
                    foreach (var gone in model.Destroy(a))
                    {
                        live.Remove(gone);
                        live.Add(world.Create());
                    }
                    break;
            }
            if (model.Mismatch(world, live) is { } problem)
                return $"step {step}: {problem}";
        }
        return Replay(world, tail, live);
    }

    static string? Replay(World world, LogTail tail, List<Entity> live)
    {
        var edges = new Dictionary<int, Edges>();
        var parents = new Dictionary<Entity, Entity>();
        foreach (var entry in tail.Read())
        {
            switch (entry.Kind)
            {
                case LogKind.Related:
                    Of(edges, entry.TypeIndex).Add(entry.Entity, entry.Other);
                    break;
                case LogKind.Unrelated:
                    Of(edges, entry.TypeIndex).Remove(entry.Entity, entry.Other);
                    break;
                case LogKind.Reparented:
                    if (entry.Other.IsDefault)
                        parents.Remove(entry.Entity);
                    else
                        parents[entry.Entity] = entry.Other;
                    break;
                case LogKind.Destroyed:
                    parents.Remove(entry.Entity);
                    break;
            }
        }
        var model = new Model();
        model.Follows.Load(Of(edges, world.Registry.Get<Follows>().Index));
        model.OwnedBy.Load(Of(edges, world.Registry.Get<OwnedBy>().Index));
        model.Allied.Load(Of(edges, world.Registry.Get<AlliedWith>().Index));
        foreach (var entity in live)
        {
            if (world.Parent(entity) != parents.GetValueOrDefault(entity))
                return $"replay: parent of {entity}";
            if (model.EdgeMismatch(world, entity, targetsOnly: true) is { } problem)
                return "replay: " + problem;
        }
        return null;

        static Edges Of(Dictionary<int, Edges> edges, int type) =>
            edges.TryGetValue(type, out var found) ? found : edges[type] = new Edges();
    }

    sealed class Edges
    {
        readonly Dictionary<Entity, List<Entity>> targets = [];
        readonly Dictionary<Entity, List<Entity>> sources = [];

        public List<Entity> Targets(Entity source) => List(targets, source);

        public List<Entity> Sources(Entity target) => List(sources, target);

        public bool Has(Entity source, Entity target) => Targets(source).Contains(target);

        public void Add(Entity source, Entity target)
        {
            Targets(source).Add(target);
            Sources(target).Add(source);
        }

        public void Remove(Entity source, Entity target)
        {
            if (Targets(source).Remove(target))
                Sources(target).Remove(source);
        }

        public void Forget(Entity entity)
        {
            foreach (var target in Targets(entity).ToList())
                Remove(entity, target);
            foreach (var source in Sources(entity).ToList())
                Remove(source, entity);
        }

        public void Load(Edges other)
        {
            foreach (var (source, list) in other.targets)
            {
                foreach (var target in list)
                    Add(source, target);
            }
        }

        static List<Entity> List(Dictionary<Entity, List<Entity>> map, Entity key) =>
            map.TryGetValue(key, out var list) ? list : map[key] = [];
    }

    sealed class Model
    {
        public readonly Edges Follows = new();
        public readonly Edges OwnedBy = new();
        public readonly Edges Allied = new();
        readonly Dictionary<Entity, Entity> parents = [];
        readonly Dictionary<Entity, List<Entity>> children = [];

        public void Relate(
            Edges edges,
            Entity source,
            Entity target,
            bool exclusive,
            bool symmetric
        )
        {
            Link(edges, source, target, exclusive);
            if (symmetric && source != target)
                Link(edges, target, source, exclusive);
        }

        static void Link(Edges edges, Entity source, Entity target, bool exclusive)
        {
            if (edges.Has(source, target))
                return;
            if (exclusive && edges.Targets(source) is { Count: > 0 } current)
                edges.Remove(source, current[0]);
            edges.Add(source, target);
        }

        public void Unrelate(Edges edges, Entity source, Entity target, bool symmetric)
        {
            edges.Remove(source, target);
            if (symmetric)
                edges.Remove(target, source);
        }

        List<Entity> Children(Entity parent) =>
            children.TryGetValue(parent, out var list) ? list : children[parent] = [];

        public bool WouldCycle(Entity child, Entity parent)
        {
            for (var up = parent; !up.IsDefault; up = parents.GetValueOrDefault(up))
            {
                if (up == child)
                    return true;
            }
            return false;
        }

        public void SetParent(Entity child, Entity parent, int index)
        {
            var old = parents.GetValueOrDefault(child);
            if (old == parent)
            {
                if (parent.IsDefault)
                    return;
                Children(parent).Remove(child);
                Insert(Children(parent), child, index);
                return;
            }
            if (!old.IsDefault)
                Children(old).Remove(child);
            if (parent.IsDefault)
            {
                parents.Remove(child);
                return;
            }
            parents[child] = parent;
            Insert(Children(parent), child, index);
        }

        static void Insert(List<Entity> list, Entity child, int index) =>
            list.Insert(index < 0 || index > list.Count ? list.Count : index, child);

        int Depth(Entity entity) =>
            parents.TryGetValue(entity, out var parent) ? Depth(parent) + 1 : 0;

        public List<Entity> Destroy(Entity root)
        {
            var gone = new List<Entity>();
            var pending = new Stack<Entity>([root]);
            while (pending.Count > 0)
            {
                var entity = pending.Pop();
                gone.Add(entity);
                foreach (var child in Children(entity))
                    pending.Push(child);
            }
            foreach (var entity in gone)
            {
                Follows.Forget(entity);
                OwnedBy.Forget(entity);
                Allied.Forget(entity);
                if (parents.Remove(entity, out var parent) && !gone.Contains(parent))
                    Children(parent).Remove(entity);
                children.Remove(entity);
            }
            return gone;
        }

        public string? Mismatch(World world, List<Entity> live)
        {
            foreach (var entity in live)
            {
                if (EdgeMismatch(world, entity, targetsOnly: false) is { } problem)
                    return problem;
                if (world.Parent(entity) != parents.GetValueOrDefault(entity))
                    return $"parent of {entity}";
                if (
                    !world
                        .Children(entity)
                        .ToArray()
                        .AsSpan()
                        .SequenceEqual(CollectionsMarshal(Children(entity)))
                )
                    return $"children of {entity}";
                if (world.Depth(entity) != Depth(entity))
                    return $"depth of {entity}: {world.Depth(entity)} vs {Depth(entity)}";
            }
            return null;
        }

        static ReadOnlySpan<Entity> CollectionsMarshal(List<Entity> list) =>
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);

        public string? EdgeMismatch(World world, Entity entity, bool targetsOnly) =>
            Check<Follows>(world, Follows, entity, targetsOnly)
            ?? Check<OwnedBy>(world, OwnedBy, entity, targetsOnly)
            ?? Check<AlliedWith>(world, Allied, entity, targetsOnly);

        static string? Check<R>(World world, Edges edges, Entity entity, bool targetsOnly)
            where R : struct, IRelation
        {
            var targets = new List<Entity>();
            foreach (var target in world.Targets<R>(entity))
                targets.Add(target);
            if (!targets.SequenceEqual(edges.Targets(entity)))
                return $"{typeof(R).Name} targets of {entity}";
            if (
                !targetsOnly
                && !world
                    .Sources<R>(entity)
                    .ToArray()
                    .AsSpan()
                    .SequenceEqual(CollectionsMarshal(edges.Sources(entity)))
            )
                return $"{typeof(R).Name} sources of {entity}";
            return null;
        }
    }
}
