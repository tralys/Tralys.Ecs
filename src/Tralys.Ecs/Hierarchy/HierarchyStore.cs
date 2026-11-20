using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

internal sealed class HierarchyStore
{
    internal struct Node
    {
        public Entity Entity;
        public Entity Parent;
        public int Children;
        public int Depth;
        public ulong DescendantVersion;
    }

    readonly EntityMap map = new();
    Node[] nodes = new Node[16];
    int count;
    int live;
    readonly Stack<int> free = new();
    readonly ListArena<Entity> children = new();

    public ChangeTracking? Raises;

    public bool IsEmpty => live == 0;

    // Never cleared: until the first node exists, no archetype row holds an ancestor bit.
    public bool EverUsed { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Node Find(Entity entity)
    {
        if (live == 0)
            return ref Unsafe.NullRef<Node>();
        var slot = map.Get(entity.Index);
        if (slot < 0)
            return ref Unsafe.NullRef<Node>();
        ref var node = ref nodes[slot];
        return ref node.Entity == entity ? ref node : ref Unsafe.NullRef<Node>();
    }

    // Growth moves the node array: refs taken before a call to Ensure are stale after it.
    public ref Node Ensure(Entity entity)
    {
        ref var found = ref Find(entity);
        if (!Unsafe.IsNullRef(ref found))
            return ref found;
        var slot = free.Count > 0 ? free.Pop() : count++;
        if (slot == nodes.Length)
            Array.Resize(ref nodes, nodes.Length * 2);
        nodes[slot] = new Node { Entity = entity, Children = -1 };
        map.Set(entity.Index, slot);
        live++;
        EverUsed = true;
        return ref nodes[slot];
    }

    public void Free(Entity entity)
    {
        var slot = map.Get(entity.Index);
        if (slot < 0 || nodes[slot].Entity != entity)
            return;
        if (nodes[slot].Children >= 0)
        {
            children.Free(nodes[slot].Children);
        }
        nodes[slot] = default;
        map.Remove(entity.Index);
        free.Push(slot);
        live--;
    }

    public bool HasChildren(Entity entity)
    {
        ref var node = ref Find(entity);
        return !Unsafe.IsNullRef(ref node)
            && node.Children >= 0
            && children.Count(node.Children) > 0;
    }

    public Entity ParentOf(Entity entity)
    {
        ref var node = ref Find(entity);
        return Unsafe.IsNullRef(ref node) ? default : node.Parent;
    }

    public ReadOnlySpan<Entity> ChildrenOf(Entity entity)
    {
        ref var node = ref Find(entity);
        return Unsafe.IsNullRef(ref node) || node.Children < 0
            ? default
            : children.Span(node.Children);
    }

    public EntityList ListChildren(Entity entity)
    {
        ref var node = ref Find(entity);
        return Unsafe.IsNullRef(ref node) || node.Children < 0
            ? default
            : new EntityList(children, node.Children);
    }

    public void AddChild(Entity parent, Entity child, int index)
    {
        ref var node = ref Find(parent);
        if (node.Children < 0)
            node.Children = children.New();
        var count = children.Count(node.Children);
        children.Insert(node.Children, index < 0 || index > count ? count : index, child);
    }

    public int RemoveChild(Entity parent, Entity child)
    {
        ref var node = ref Find(parent);
        if (Unsafe.IsNullRef(ref node) || node.Children < 0)
            return -1;
        var at = children.Span(node.Children).IndexOf(child);
        if (at >= 0)
        {
            children.RemoveAt(node.Children, at);
        }
        return at;
    }

    // Stops at the first ancestor already this new: every ancestor above it was raised at least as far when it was.
    public void RaiseAncestors(Entity entity, ulong version)
    {
        for (var parent = ParentOf(entity); !parent.IsDefault; parent = ParentOf(parent))
        {
            ref var node = ref Find(parent);
            if (Unsafe.IsNullRef(ref node))
                return;
            var prior = Versions.Max(ref node.DescendantVersion, version);
            if (prior >= version)
                return;
            if (Raises is { TakesRaises: true } raises)
                raises.Raised(parent, prior);
        }
    }

    public HierarchyStore Clone()
    {
        var copy = new HierarchyStore();
        copy.Load(this);
        return copy;
    }

    public void Load(HierarchyStore source)
    {
        map.Load(source.map);
        Copies.Load(ref nodes, source.nodes, source.count);
        count = source.count;
        live = source.live;
        Copies.Load(free, source.free);
        children.Load(source.children);
        EverUsed |= source.EverUsed;
    }

    public void Clear()
    {
        map.Clear();
        Array.Clear(nodes, 0, count);
        count = 0;
        live = 0;
        free.Clear();
        children.Clear();
    }
}

internal static class Versions
{
    // Returns the value before the call, so a caller can tell whether it raised anything.
    public static ulong Max(ref ulong location, ulong value)
    {
        var current = Volatile.Read(ref location);
        while (current < value)
        {
            var seen = Interlocked.CompareExchange(ref location, value, current);
            if (seen == current)
                return current;
            current = seen;
        }
        return current;
    }
}
