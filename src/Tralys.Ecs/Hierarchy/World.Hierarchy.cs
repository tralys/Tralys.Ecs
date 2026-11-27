using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    internal readonly HierarchyStore hierarchy = new();
    Entity[] walk = new Entity[16];
    bool[] inheritedScratch = [];

    /// <summary>Makes <paramref name="child"/> a child of <paramref name="parent"/> at <paramref name="index"/>; -1 puts it last, also when it
    /// is already a child of <paramref name="parent"/>. The moved subtree's depth and inherited bits follow at once; inside a query walk the
    /// move waits until the walk closes, and for an entity an observer created, or while either entity has changes waiting, until the next
    /// playback; reads see it then.</summary>
    /// <exception cref="ArgumentException"><paramref name="parent"/> is null; use <see cref="ClearParent"/>.</exception>
    /// <exception cref="TargetPolicyException">Under a dying <paramref name="parent"/> the subtree would take a <see cref="TargetPolicy.Throw"/>
    /// target from a source that outlives it, or out of a dying one it would revive a source of such an edge onto a dying
    /// target.</exception>
    /// <exception cref="InvalidOperationException">The move would make a cycle.</exception>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
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
            Reparent(child, parent, index, default);
        VerifyInvariants();
    }

    /// <summary>Takes <paramref name="child"/> out of its parent's children, making it a root; inside a query walk once the walk closes, and
    /// while it has changes waiting at the next playback.</summary>
    /// <exception cref="TargetPolicyException">Leaving a dying parent would revive the source of a <see cref="TargetPolicy.Throw"/> edge onto
    /// a dying target.</exception>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public void ClearParent(
        Entity child,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        if (!RecordsReparent(child, default, -1))
            Reparent(child, default, -1, default);
        VerifyInvariants();
    }

    // A call is checked against the hierarchy as recorded, so the reparents that wait never form a cycle.
    bool RecordsReparent(Entity child, Entity parent, int index)
    {
        CheckWritable();
        Structural();
        ref var slot = ref Slot(child);
        if (!RecordsLifecycle(slot) && (parent.IsDefault || !RecordsLifecycle(Slot(parent))))
        {
            if (!parent.IsDefault && recordedParents.Count > 0)
                CheckCycle(child, parent, asRecorded: true);
            return false;
        }
        if (!parent.IsDefault)
            CheckParentable(child, parent, asRecorded: true);
        stage.Record(CommandKind.SetParent, child, payload: index, other: parent);
        NoteRecordedParent(child, parent);
        MarkPending(ref slot);
        return true;
    }

    void CheckParentable(Entity child, Entity parent, bool asRecorded)
    {
        Slot(parent);
        if (parent == child)
            throw new InvalidOperationException($"{child} cannot be its own parent.");
        CheckCycle(child, parent, asRecorded);
        // A recorded destroy of the parent is judged with this reparent at playback, so only settled liveness counts here.
        if (!MayLive(parent))
            CheckTargetPolicy(child);
    }

    void CheckCycle(Entity child, Entity parent, bool asRecorded)
    {
        for (
            var up = parent;
            !up.IsDefault;
            up = asRecorded ? ParentAsRecorded(up) : hierarchy.ParentOf(up)
        )
        {
            if (up == child)
                throw new InvalidOperationException(
                    $"Parenting {child} under {parent} would make a cycle."
                );
        }
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

    internal void Reparent(Entity child, Entity parent, int index, LifecycleReason reason)
    {
        CheckWritable();
        Slot(child);
        if (!parent.IsDefault)
            CheckParentable(child, parent, asRecorded: false);
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
        if (
            (parent.IsDefault || MayLive(parent))
            && !OwnsDestroy(allocator.Slots[child.Index])
            && UnderPendingDestroy(child)
            && RevivalFault(child) is { } revival
        )
            throw revival;
        var wasPending =
            changes.Wants(-1, LogKind.DestroyCancelled)
            && StateOf(child) == LifecycleState.PendingDestroy;
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
    internal void LogSpared(Entity root)
    {
        var count = 0;
        Push(ref count, root);
        while (count > 0)
        {
            var entity = walk[--count];
            if (StateOf(entity) == LifecycleState.PendingDestroy)
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
        InheritedOf(hierarchy.ParentOf(child));
        if (TakeInherited(child, archetype, slot.Row) && hierarchy.HasChildren(child))
            RefreshChildren(child);
    }

    void InheritedOf(Entity parent)
    {
        var kinds = layout.InheritedCount;
        if (inheritedScratch.Length < kinds)
            inheritedScratch = new bool[kinds];
        for (var kind = 0; kind < kinds; kind++)
            inheritedScratch[kind] = !parent.IsDefault && Effective(parent, kind);
    }

    // True when a bit the child does not set itself flipped, so its own children follow.
    bool TakeInherited(Entity child, Archetype archetype, int row)
    {
        var changed = false;
        var pendingFlipped = false;
        for (var kind = 0; kind < layout.InheritedCount; kind++)
        {
            var on = inheritedScratch[kind];
            if (archetype.HasAncestor(kind, row) == on)
                continue;
            archetype.SetAncestor(kind, row, on);
            var flipped = !SelfBit(archetype, row, kind);
            changed |= flipped;
            pendingFlipped |= flipped && kind == 0;
        }
        if (pendingFlipped)
            RefreshAllIndexes(child);
        return changed;
    }

    void RefreshUnplaced(Entity entity)
    {
        RefreshAllIndexes(entity);
        if (hierarchy.HasChildren(entity))
            RefreshChildren(entity);
    }

    internal void RefreshChildren(Entity root)
    {
        var count = 0;
        Push(ref count, root);
        while (count > 0)
        {
            var parent = walk[--count];
            InheritedOf(parent);
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
                if (!TakeInherited(child, archetype, slot.Row))
                    continue;
                hierarchy.RaiseAncestors(child, version);
                if (hierarchy.HasChildren(child))
                    Push(ref count, child);
            }
        }
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
