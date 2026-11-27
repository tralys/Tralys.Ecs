using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

internal abstract class RelationStore(int type, ComponentDescriptor descriptor)
{
    public readonly int Type = type;
    public readonly bool Exclusive = (descriptor.RelationTraits & RelationTraits.Exclusive) != 0;
    public readonly bool Symmetric = (descriptor.RelationTraits & RelationTraits.Symmetric) != 0;
    public readonly TargetPolicy Policy = descriptor.OnTargetDestroyed;

    public abstract ReadOnlySpan<Entity> SourcesOf(Entity target);

    public abstract EntityList ListSources(Entity target);

    public abstract bool HasTargets { get; }

    // A free node reads as the null entity.
    public abstract int TargetNodes { get; }

    public abstract Entity TargetNode(int node);

    public abstract void ApplyRelate(
        World world,
        Entity source,
        Entity target,
        ref byte payload,
        LifecycleReason reason
    );

    public abstract bool Unrelate(
        World world,
        Entity source,
        Entity target,
        LifecycleReason reason
    );

    public abstract void ReleaseEntity(World world, Entity entity, LifecycleReason reason);

    public abstract void ClearFrom(World world, Entity source, LifecycleReason reason);

    public abstract void ClearTo(World world, Entity target, LifecycleReason reason);

    public abstract void ApplyRetarget(
        World world,
        Entity source,
        Entity oldTarget,
        Entity newTarget
    );

    public abstract void RaiseSources(Entity target, ulong version);

    public abstract void Clear();

    public abstract RelationStore Clone();

    public abstract void Load(RelationStore source);

    public abstract int EdgeCount(Entity source);

    public abstract Entity EdgeTarget(Entity source, int edge);

    public abstract ref byte EdgePayload(Entity source, int edge);
}

internal sealed class RelationStore<R> : RelationStore
    where R : struct
{
    internal struct Edge
    {
        public Entity Target;
        public R Payload;
    }

    struct Node
    {
        public Entity Entity;
        public int List;
        public ulong RelatedVersion;
    }

    readonly ComponentDescriptor descriptor;
    readonly EntityMap sourceMap = new();
    readonly EntityMap targetMap = new();
    Node[] sources = new Node[8];
    Node[] targets = new Node[8];
    int sourceCount;
    int targetCount;
    readonly Stack<int> freeSources = new();
    readonly Stack<int> freeTargets = new();
    readonly ListArena<Edge> edges = new();
    readonly ListArena<Entity> reverse = new();
    Entity[] scratch = new Entity[8];

    public RelationStore(int type, ComponentDescriptor descriptor)
        : base(type, descriptor) => this.descriptor = descriptor;

    public override RelationStore Clone()
    {
        var copy = new RelationStore<R>(Type, descriptor);
        copy.Load(this);
        return copy;
    }

    public override void Load(RelationStore other)
    {
        var source = (RelationStore<R>)other;
        sourceMap.Load(source.sourceMap);
        targetMap.Load(source.targetMap);
        Copies.Load(ref sources, source.sources, source.sourceCount);
        Copies.Load(ref targets, source.targets, source.targetCount);
        sourceCount = source.sourceCount;
        targetCount = source.targetCount;
        Copies.Load(freeSources, source.freeSources);
        Copies.Load(freeTargets, source.freeTargets);
        edges.Load(source.edges);
        reverse.Load(source.reverse);
    }

    public ReadOnlySpan<Edge> EdgesOf(Entity source)
    {
        ref var node = ref Find(sources, sourceMap, source);
        return Unsafe.IsNullRef(ref node) ? default : edges.Span(node.List);
    }

    public override ReadOnlySpan<Entity> SourcesOf(Entity target)
    {
        ref var node = ref Find(targets, targetMap, target);
        return Unsafe.IsNullRef(ref node) ? default : reverse.Span(node.List);
    }

    public override EntityList ListSources(Entity target)
    {
        ref var node = ref Find(targets, targetMap, target);
        return Unsafe.IsNullRef(ref node) ? default : new EntityList(reverse, node.List);
    }

    public ListArena<Edge> EdgeLists => edges;

    public int EdgeListOf(Entity source)
    {
        ref var node = ref Find(sources, sourceMap, source);
        return Unsafe.IsNullRef(ref node) ? -1 : node.List;
    }

    public override bool HasTargets => targetCount > freeTargets.Count;

    public override int TargetNodes => targetCount;

    public override Entity TargetNode(int node) => targets[node].Entity;

    public override int EdgeCount(Entity source) => EdgesOf(source).Length;

    public override Entity EdgeTarget(Entity source, int edge) => EdgesOf(source)[edge].Target;

    public override ref byte EdgePayload(Entity source, int edge)
    {
        ref var node = ref Find(sources, sourceMap, source);
        return ref Unsafe.As<R, byte>(ref edges.At(node.List, edge).Payload);
    }

    public ulong RelatedVersionOf(Entity source)
    {
        ref var node = ref Find(sources, sourceMap, source);
        return Unsafe.IsNullRef(ref node) ? 0 : Volatile.Read(ref node.RelatedVersion);
    }

    public int IndexOf(Entity source, Entity target)
    {
        var list = EdgesOf(source);
        for (var i = 0; i < list.Length; i++)
        {
            if (list[i].Target == target)
                return i;
        }
        return -1;
    }

    public void Relate(
        World world,
        Entity source,
        Entity target,
        in R payload,
        LifecycleReason reason = default
    )
    {
        world.CheckRelatable(Type, source, target);
        Join(world, source, target, payload, reason);
    }

    void Join(World world, Entity source, Entity target, in R payload, LifecycleReason reason)
    {
        Link(world, source, target, payload, reason);
        if (Symmetric && source != target)
            Link(world, target, source, payload, reason);
    }

    void Link(World world, Entity source, Entity target, in R payload, LifecycleReason reason)
    {
        var at = IndexOf(source, target);
        if (at >= 0)
        {
            ref var node = ref Find(sources, sourceMap, source);
            ref var stored = ref edges.At(node.List, at).Payload;
            var incoming = payload;
            if (
                ComponentOpsOf<R>.Equals(
                    ref Unsafe.As<R, byte>(ref stored),
                    ref Unsafe.As<R, byte>(ref incoming)
                )
            )
                return;
            var departing = stored;
            if (world.layout.OwnsBuffers[Type])
                world.FreeReplacedBuffers(
                    Type,
                    ref Unsafe.As<R, byte>(ref stored),
                    ref Unsafe.As<R, byte>(ref incoming)
                );
            stored = incoming;
            Versions.Max(ref node.RelatedVersion, world.version);
            world.changes.Append(LogKind.RelationChanged, source, Type, reason, target.Bits);
            world.observers.QueueRelationChanged(
                source,
                target,
                Type,
                ref Unsafe.As<R, byte>(ref departing),
                ref Unsafe.As<R, byte>(ref incoming),
                reason
            );
            return;
        }
        if (Exclusive)
        {
            var current = EdgesOf(source);
            if (!current.IsEmpty)
                Unrelate(world, source, current[0].Target, default);
        }
        ref var from = ref Ensure(ref sources, sourceMap, ref sourceCount, freeSources, source);
        Versions.Max(ref from.RelatedVersion, world.version);
        edges.Add(from.List, new Edge { Target = target, Payload = payload });
        ref var to = ref Ensure(ref targets, targetMap, ref targetCount, freeTargets, target);
        reverse.Add(to.List, source);
        var copy = payload;
        world.changes.Append(LogKind.Related, source, Type, reason, target.Bits);
        world.observers.QueueRelation(
            source,
            target,
            Type,
            ref Unsafe.As<R, byte>(ref copy),
            true,
            reason
        );
    }

    public override void ApplyRelate(
        World world,
        Entity source,
        Entity target,
        ref byte payload,
        LifecycleReason reason
    ) => Relate(world, source, target, Unsafe.As<byte, R>(ref payload), reason);

    public override bool Unrelate(World world, Entity source, Entity target, LifecycleReason reason)
    {
        var removed = Unlink(world, source, target, reason, release: true);
        if (Symmetric && source != target)
            removed |= Unlink(world, target, source, reason, release: true);
        return removed;
    }

    public override void ApplyRetarget(
        World world,
        Entity source,
        Entity oldTarget,
        Entity newTarget
    )
    {
        var at = IndexOf(source, oldTarget);
        if (at < 0)
            throw new InvalidOperationException(
                $"{source} has no {descriptor.Name} edge to {oldTarget} to retarget."
            );
        var payload = EdgesOf(source)[at].Payload;
        Retarget(world, source, oldTarget, newTarget, payload);
    }

    public void Retarget(
        World world,
        Entity source,
        Entity oldTarget,
        Entity newTarget,
        in R payload
    )
    {
        world.CheckRelatable(Type, source, newTarget);
        Unlink(world, source, oldTarget, default, release: false);
        if (Symmetric && source != oldTarget)
            Unlink(world, oldTarget, source, default, release: false);
        Join(world, source, newTarget, payload, default);
    }

    bool Unlink(World world, Entity source, Entity target, LifecycleReason reason, bool release)
    {
        var at = IndexOf(source, target);
        if (at < 0)
            return false;
        ref var from = ref Find(sources, sourceMap, source);
        Versions.Max(ref from.RelatedVersion, world.version);
        var payload = edges.At(from.List, at).Payload;
        edges.RemoveAt(from.List, at);
        ref var to = ref Find(targets, targetMap, target);
        var back = reverse.Span(to.List).IndexOf(source);
        reverse.RemoveAt(to.List, back);
        if (reverse.Count(to.List) == 0)
            Free(targets, targetMap, freeTargets, reverse, target);
        world.changes.Append(LogKind.Unrelated, source, Type, reason, target.Bits);
        if (release && world.layout.OwnsBuffers[Type])
            world.FreeBuffers(Type, ref Unsafe.As<R, byte>(ref payload));
        world.observers.QueueRelation(
            source,
            target,
            Type,
            ref Unsafe.As<R, byte>(ref payload),
            false,
            reason
        );
        return true;
    }

    public override void ClearFrom(World world, Entity source, LifecycleReason reason)
    {
        while (EdgesOf(source) is { IsEmpty: false } list)
            Unrelate(world, source, list[0].Target, reason);
    }

    public override void ClearTo(World world, Entity target, LifecycleReason reason)
    {
        while (SourcesOf(target) is { IsEmpty: false } list)
            Unrelate(world, list[0], target, reason);
    }

    public override void ReleaseEntity(World world, Entity entity, LifecycleReason reason)
    {
        var incoming = SourcesOf(entity);
        var count = incoming.Length;
        if (scratch.Length < count)
            scratch = new Entity[Math.Max(count, scratch.Length * 2)];
        incoming.CopyTo(scratch);
        ClearFrom(world, entity, reason);
        for (var i = 0; i < count; i++)
            Unrelate(world, scratch[i], entity, reason);
        Array.Clear(scratch, 0, count);
        Free(sources, sourceMap, freeSources, edges, entity);
    }

    public override void RaiseSources(Entity target, ulong version)
    {
        foreach (var source in SourcesOf(target))
        {
            ref var node = ref Find(sources, sourceMap, source);
            if (!Unsafe.IsNullRef(ref node))
                Versions.Max(ref node.RelatedVersion, version);
        }
    }

    public override void Clear()
    {
        sourceMap.Clear();
        targetMap.Clear();
        Array.Clear(sources, 0, sourceCount);
        Array.Clear(targets, 0, targetCount);
        sourceCount = targetCount = 0;
        freeSources.Clear();
        freeTargets.Clear();
        edges.Clear();
        reverse.Clear();
    }

    static ref Node Find(Node[] nodes, EntityMap map, Entity entity)
    {
        var slot = map.Get(entity.Index);
        if (slot < 0)
            return ref Unsafe.NullRef<Node>();
        ref var node = ref nodes[slot];
        return ref node.Entity == entity ? ref node : ref Unsafe.NullRef<Node>();
    }

    ref Node Ensure(ref Node[] nodes, EntityMap map, ref int count, Stack<int> free, Entity entity)
    {
        ref var found = ref Find(nodes, map, entity);
        if (!Unsafe.IsNullRef(ref found))
            return ref found;
        var slot = free.Count > 0 ? free.Pop() : count++;
        if (slot == nodes.Length)
            Array.Resize(ref nodes, nodes.Length * 2);
        var list = ReferenceEquals(nodes, sources) ? edges.New() : reverse.New();
        nodes[slot] = new Node { Entity = entity, List = list };
        map.Set(entity.Index, slot);
        return ref nodes[slot];
    }

    static void Free<T>(
        Node[] nodes,
        EntityMap map,
        Stack<int> free,
        ListArena<T> lists,
        Entity entity
    )
    {
        var slot = map.Get(entity.Index);
        if (slot < 0 || nodes[slot].Entity != entity)
            return;
        lists.Free(nodes[slot].List);
        nodes[slot] = default;
        map.Remove(entity.Index);
        free.Push(slot);
    }
}

/// <summary>The targets of one source in relation <typeparamref name="R"/>, in the order they were related. It throws once an edge of
/// <typeparamref name="R"/> from that source is made or broken; take <see cref="World.SnapshotTargets{R}"/> to edit them while
/// walking.</summary>
public readonly ref struct RelationTargets<R>
    where R : struct, IRelation
{
    readonly ListArena<RelationStore<R>.Edge>? lists;
    readonly int list;
    readonly int stamp;

    internal RelationTargets(ListArena<RelationStore<R>.Edge> lists, int list)
    {
        this.lists = lists;
        this.list = list;
        stamp = lists.StampOf(list);
    }

    ReadOnlySpan<RelationStore<R>.Edge> Edges => lists is not null ? lists.Span(list) : default;

    /// <summary>How many targets the source has.</summary>
    public int Count
    {
        get
        {
            Check();
            return Edges.Length;
        }
    }

    /// <summary>The target of the edge at <paramref name="index"/>.</summary>
    public Entity this[int index]
    {
        get
        {
            Check();
            return Edges[index].Target;
        }
    }

    /// <summary>The payload of the edge at <paramref name="index"/>; the ref is valid while the list is.</summary>
    public ref readonly R Payload(int index)
    {
        Check();
        return ref Edges[index].Payload;
    }

    /// <summary>Walks the targets in the order they were related; it throws once an edge from the source is made or broken.</summary>
    public Enumerator GetEnumerator() => new(this);

    readonly void Check()
    {
        if (lists is not null && lists.StampOf(list) != stamp)
            ThrowChanged();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowChanged() =>
        throw new InvalidOperationException(
            "The relation changed after its targets were read; take SnapshotTargets before an edit that makes or breaks an edge."
        );

    /// <summary>Walks a <see cref="RelationTargets{R}"/>.</summary>
    public ref struct Enumerator
    {
        readonly RelationTargets<R> targets;
        int index;

        internal Enumerator(RelationTargets<R> targets)
        {
            this.targets = targets;
            index = -1;
        }

        /// <summary>The target at the current position.</summary>
        public readonly Entity Current => targets[index];

        /// <summary>Advances; false past the last target.</summary>
        public bool MoveNext() => ++index < targets.Count;
    }
}
