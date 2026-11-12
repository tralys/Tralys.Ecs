using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    internal readonly HierarchyStore hierarchy = new();
    Entity[] walk = new Entity[16];
    bool[] inheritedScratch = [];

    /// <summary>Makes <paramref name="child"/> a child of <paramref name="parent"/> at <paramref name="index"/>; -1 puts it last, also when it
    /// is already a child of <paramref name="parent"/>. The moved subtree's depth and inherited bits follow at once; for an entity an
    /// observer created, or while either entity has changes waiting, the move waits for the next playback and reads see it then.</summary>
    /// <exception cref="ArgumentException"><paramref name="parent"/> is null; use <see cref="ClearParent"/>.</exception>
    /// <exception cref="InvalidOperationException">The move would make a cycle, or a serial query body is running.</exception>
    [Effect(Effects.Structural | Effects.Mutates)]
    public void SetParent(
        Entity child,
        Entity parent,
        int index = -1,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        CheckParent(parent);
        At(file, line);
        if (!RecordsReparent(child, parent, index))
            Reparent(child, parent, index, default, guardTargets: true);
    }

    /// <summary>Takes <paramref name="child"/> out of its parent's children, making it a root; while it has changes waiting, at the next
    /// playback.</summary>
    /// <exception cref="InvalidOperationException">A serial query body is running.</exception>
    [Effect(Effects.Structural | Effects.Mutates)]
    public void ClearParent(
        Entity child,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        if (!RecordsReparent(child, default, -1))
            Reparent(child, default, -1, default, guardTargets: true);
    }

    bool RecordsReparent(Entity child, Entity parent, int index)
    {
        CheckWritable();
        CheckStructural();
        ref var slot = ref Slot(child);
        if (!DefersLifecycle(slot) && (parent.IsDefault || !DefersLifecycle(Slot(parent))))
            return false;
        if (!parent.IsDefault)
        {
            Slot(parent);
            if (parent == child)
                throw new InvalidOperationException($"{child} cannot be its own parent.");
            if (!IsLive(parent))
                CheckTargetPolicy(child);
        }
        stage.Record(CommandKind.SetParent, child, payload: index, other: parent);
        slot.Pending = true;
        return true;
    }

    internal static void CheckParent(Entity parent)
    {
        if (parent.IsDefault)
            throw new ArgumentException(
                "The parent is null; call ClearParent to make an entity a root.",
                nameof(parent)
            );
    }

    /// <summary>The inherited tag <typeparamref name="T"/> is in effect on the entity: enabled on it or on an ancestor. This is the bit
    /// queries match.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not declared <c>Inherited</c>.</exception>
    [Effect(Effects.None)]
    public bool IsEffective<T>(Entity entity)
        where T : struct, ITag, IEnableable
    {
        Slot(entity);
        var type = registry.IndexOf<T>();
        var kind = layout.InheritedSlot[type];
        if (kind <= 0)
            throw new InvalidOperationException(
                $"{typeof(T).Name} is not an inherited tag; read it with Has."
            );
        return overlay.IsEmpty ? Effective(entity, kind) : EffectiveWithPending(entity, type, kind);
    }

    bool EffectiveWithPending(Entity entity, int type, int kind)
    {
        for (var node = entity; !node.IsDefault; node = hierarchy.ParentOf(node))
        {
            if (overlay.TryGet(node.Index, type, out var present, out var enabled, out _))
            {
                if (present && enabled)
                    return true;
                continue;
            }
            ref var slot = ref allocator.Slots[node.Index];
            if (slot.Archetype is { } archetype && SelfBit(archetype, slot.Row, kind))
                return true;
        }
        return false;
    }

    internal void Reparent(
        Entity child,
        Entity parent,
        int index,
        LifecycleReason reason,
        bool guardTargets
    )
    {
        CheckWritable();
        Slot(child);
        if (!parent.IsDefault)
        {
            Slot(parent);
            if (parent == child)
                throw new InvalidOperationException($"{child} cannot be its own parent.");
            for (var up = parent; !up.IsDefault; up = hierarchy.ParentOf(up))
            {
                if (up == child)
                    throw new InvalidOperationException(
                        $"Parenting {child} under {parent} would make a cycle."
                    );
            }
            if (guardTargets && !IsLive(parent))
                CheckTargetPolicy(child);
        }
        var old = hierarchy.ParentOf(child);
        if (old == parent)
        {
            if (parent.IsDefault)
                return;
            var siblings = hierarchy.ChildrenOf(parent);
            if (
                siblings.IndexOf(child)
                == (index < 0 || index >= siblings.Length ? siblings.Length - 1 : index)
            )
                return;
            hierarchy.RemoveChild(parent, child);
            hierarchy.AddChild(parent, child, index);
            observers.QueueHierarchy(child, parent, HierarchyEvent.ChildMoved, reason);
            changes.Append(LogKind.Reparented, child, -1, reason, parent.Bits);
            RaiseDescendants(child, version);
            return;
        }
        var wasPending = changes.Logging && StateOf(child) == LifecycleState.PendingDestroy;
        hierarchy.Ensure(child);
        if (!parent.IsDefault)
            hierarchy.Ensure(parent);
        if (!old.IsDefault)
        {
            hierarchy.RemoveChild(old, child);
            observers.QueueHierarchy(child, old, HierarchyEvent.ChildRemoved, reason);
            hierarchy.RaiseAncestors(child, version);
        }
        hierarchy.Find(child).Parent = parent;
        if (!parent.IsDefault)
        {
            hierarchy.AddChild(parent, child, index);
            observers.QueueHierarchy(child, parent, HierarchyEvent.ChildAdded, reason);
        }
        SetDepths(child, parent.IsDefault ? 0 : hierarchy.Find(parent).Depth + 1);
        InheritFromParent(child);
        if (partitionsUsed)
            RefreshPartitions(child, reason);
        RefreshParentIndexes(child);
        changes.Append(LogKind.Reparented, child, -1, reason, parent.Bits);
        if (wasPending)
            LogSpared(child);
        RaiseDescendants(child, version);
        ThrowIndexConflicts();
    }

    // Every caller passes a root that was pending destroy, so each entity found no longer pending was spared.
    internal void LogSpared(Entity root, HashSet<Entity>? logged = null)
    {
        if (!changes.Logging)
            return;
        var count = 0;
        Push(ref count, root);
        while (count > 0)
        {
            var entity = walk[--count];
            if (StateOf(entity) == LifecycleState.PendingDestroy || logged?.Add(entity) == false)
                continue;
            changes.Append(LogKind.DestroyCancelled, entity, -1, default);
            foreach (var child in hierarchy.ChildrenOf(entity))
                Push(ref count, child);
        }
    }

    /// <summary>The entity's parent, or null for a root.</summary>
    [Effect(Effects.None)]
    public Entity Parent(Entity entity)
    {
        return hierarchy.ParentOf(entity);
    }

    /// <summary>Children in order. The list throws once <paramref name="entity"/> gains, loses or reorders a child, though one read while
    /// it had none may stay empty; take <see cref="SnapshotChildren"/> to edit them while walking.</summary>
    [Effect(Effects.None)]
    public EntityList Children(Entity entity)
    {
        return hierarchy.ListChildren(entity);
    }

    /// <summary>Distance from the root: 0 for an entity without a parent.</summary>
    [Effect(Effects.None)]
    public int Depth(Entity entity)
    {
        return DepthOf(entity);
    }

    internal int DepthOf(Entity entity)
    {
        ref var node = ref hierarchy.Find(entity);
        return Unsafe.IsNullRef(ref node) ? 0 : node.Depth;
    }

    void RaiseDescendants(Entity child, ulong at)
    {
        ref var node = ref hierarchy.Find(child);
        var moved = Unsafe.IsNullRef(ref node) ? at : Math.Max(at, node.DescendantVersion);
        hierarchy.RaiseAncestors(child, moved);
    }

    void SetDepths(Entity root, int depth)
    {
        hierarchy.Find(root).Depth = depth;
        var count = 0;
        Push(ref count, root);
        while (count > 0)
        {
            var parent = walk[--count];
            var below = hierarchy.Find(parent).Depth + 1;
            foreach (var child in hierarchy.ChildrenOf(parent))
            {
                hierarchy.Find(child).Depth = below;
                Push(ref count, child);
            }
        }
    }

    void Push(ref int count, Entity entity)
    {
        if (count == walk.Length)
            Array.Resize(ref walk, count * 2);
        walk[count++] = entity;
    }

    bool SelfBit(Archetype archetype, int row, int kind)
    {
        if (kind == 0)
            return archetype.IsPendingDestroy(row);
        var enable = archetype.EnableSlot(layout.InheritedTypes[kind]);
        return enable >= 0 && archetype.IsEnabled(enable, row);
    }

    bool Effective(Entity entity, int kind)
    {
        ref var slot = ref allocator.Slots[entity.Index];
        if (slot.Archetype is not { } archetype)
            return kind == 0 && UnderPendingDestroy(entity);
        return archetype.HasAncestor(kind, slot.Row) || SelfBit(archetype, slot.Row, kind);
    }

    internal void InheritFromParent(Entity child)
    {
        ref var slot = ref allocator.Slots[child.Index];
        if (slot.Archetype is not { } archetype)
        {
            RefreshUnplaced(child);
            return;
        }
        var parent = hierarchy.ParentOf(child);
        var changed = false;
        var pendingFlipped = false;
        for (var kind = 0; kind < layout.InheritedCount; kind++)
        {
            var on = !parent.IsDefault && Effective(parent, kind);
            if (archetype.HasAncestor(kind, slot.Row) == on)
                continue;
            archetype.SetAncestor(kind, slot.Row, on);
            var flipped = !SelfBit(archetype, slot.Row, kind);
            changed |= flipped;
            pendingFlipped |= flipped && kind == 0;
        }
        if (pendingFlipped)
            RefreshAllIndexes(child);
        if (changed && hierarchy.HasChildren(child))
            RefreshChildren(child);
    }

    void RefreshUnplaced(Entity entity)
    {
        RefreshAllIndexes(entity);
        if (hierarchy.HasChildren(entity))
            RefreshChildren(entity);
    }

    internal void RefreshChildren(Entity root)
    {
        var kinds = layout.InheritedCount;
        if (inheritedScratch.Length < kinds)
            inheritedScratch = new bool[kinds];
        var count = 0;
        Push(ref count, root);
        while (count > 0)
        {
            var parent = walk[--count];
            for (var kind = 0; kind < kinds; kind++)
                inheritedScratch[kind] = Effective(parent, kind);
            foreach (var child in hierarchy.ChildrenOf(parent))
            {
                ref var slot = ref allocator.Slots[child.Index];
                if (slot.Archetype is not { } archetype)
                {
                    RefreshAllIndexes(child);
                    if (hierarchy.HasChildren(child))
                        Push(ref count, child);
                    continue;
                }
                var changed = false;
                var pendingFlipped = false;
                for (var kind = 0; kind < kinds; kind++)
                {
                    var on = inheritedScratch[kind];
                    if (archetype.HasAncestor(kind, slot.Row) == on)
                        continue;
                    archetype.SetAncestor(kind, slot.Row, on);
                    var flipped = !SelfBit(archetype, slot.Row, kind);
                    changed |= flipped;
                    pendingFlipped |= flipped && kind == 0;
                }
                if (pendingFlipped)
                    RefreshAllIndexes(child);
                if (!changed)
                    continue;
                hierarchy.RaiseAncestors(child, version);
                if (hierarchy.HasChildren(child))
                    Push(ref count, child);
            }
        }
    }

    // Children first, so a subtree is released bottom-up and each parent is still whole while its children go.
    void GatherSubtree(Entity root, List<Entity> into)
    {
        if (hierarchy.IsEmpty)
        {
            gathered[root.Index] = Gathered;
            into.Add(root);
            return;
        }
        var start = into.Count;
        var count = 0;
        Push(ref count, root);
        while (count > 0)
        {
            var entity = walk[--count];
            if (gathered[entity.Index] != 0)
                continue;
            gathered[entity.Index] = Gathered;
            into.Add(entity);
            ref var slot = ref allocator.Slots[entity.Index];
            if (slot.State != LifecycleState.PendingDestroy)
            {
                inheriting.Add((entity, slot.State));
                slot.State = LifecycleState.PendingDestroy;
                slot.DestroyReason = LifecycleReason.Cascade;
                attributed?.InheritDestroy(entity.Index, root.Index);
            }
            foreach (var child in hierarchy.ChildrenOf(entity))
                Push(ref count, child);
        }
        into.Reverse(start, into.Count - start);
    }

    void ReleaseHierarchy(Entity entity, LifecycleReason reason)
    {
        ref var node = ref hierarchy.Find(entity);
        if (Unsafe.IsNullRef(ref node))
            return;
        var parent = node.Parent;
        if (!parent.IsDefault)
        {
            hierarchy.RemoveChild(parent, entity);
            observers.QueueHierarchy(entity, parent, HierarchyEvent.ChildRemoved, reason);
            hierarchy.RaiseAncestors(entity, version);
        }
        foreach (var child in hierarchy.ChildrenOf(entity))
        {
            hierarchy.Find(child).Parent = default;
            SetDepths(child, 0);
            InheritFromParent(child);
            RefreshParentIndexes(child);
        }
        hierarchy.Free(entity);
    }
}
