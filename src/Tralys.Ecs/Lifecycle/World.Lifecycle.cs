using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    readonly List<Entity> pendingDestroys = [];
    readonly List<Entity> finalising = [];
    readonly List<Entity> cascadeRoots = [];

    bool planFrozen;

    static readonly LogKinds SpawnReports = LogKinds.Of(LogKind.Added);

    readonly List<BundleInfo?> bundles = [];

    /// <summary>Creates an entity. Inside a query walk or an observer it is reserved: usable by handle at once, matched by queries once the
    /// walk closes or after playback.</summary>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public Entity Create([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        At(file, line);
        CheckWritable();
        return NewEntity();
    }

    internal Entity NewEntity()
    {
        Structural();
        if (RecordsCreate)
        {
            var reserved = allocator.Allocate(LifecycleState.Reserved);
            Stage(CommandKind.Create, reserved);
            return reserved;
        }
        var entity = allocator.Allocate(LifecycleState.Alive);
        Place(entity, ref allocator.Slots[entity.Index], root);
        changes.Append(LogKind.Created, entity, -1, default);
        return entity;
    }

    /// <summary>Creates an entity with a whole bundle in one archetype move; inside a query walk or an observer it is reserved, as
    /// <see cref="Create(string, int)"/> makes it.</summary>
    /// <exception cref="InvalidOperationException">A singleton in the bundle is held.</exception>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public Entity Create<TBundle>(
        in TBundle bundle,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where TBundle : struct, IBundle
    {
        At(file, line);
        CheckWritable();
        Structural();
        var info = BundleFor<TBundle>();
        foreach (var type in info.Singletons)
        {
            if (SingletonRival(default, layout.SingletonSlot[type]) is { IsDefault: false } holder)
                throw new InvalidOperationException(
                    SingletonConflict(type, holder, "a new entity")
                );
        }
        if (RecordsCreate)
        {
            var reserved = NewEntity();
            var recorder = new BundleWriter(stage, reserved);
            bundle.Write(ref recorder);
            foreach (var type in info.SignatureTypes)
            {
                if (layout.Class[type] == StorageClass.SignatureTag)
                    Stage(CommandKind.Set, reserved, type);
            }
            foreach (var type in info.SparseTags)
                Stage(CommandKind.Set, reserved, type);
            foreach (var type in info.Singletons)
                ClaimSingleton(reserved, type);
            foreach (var type in info.IndexedTypes)
                RefreshIndexes(reserved, type);
            ThrowIndexConflicts();
            return reserved;
        }

        var archetype = info.Archetype ??= ArchetypeFor(
            root.Signature.WithAll(info.SignatureTypes, detached: false)
        );
        var entity = allocator.Allocate(LifecycleState.Alive);
        ref var slot = ref allocator.Slots[entity.Index];
        Place(entity, ref slot, archetype);
        changes.Append(LogKind.Created, entity, -1, default);
        var row = slot.Row;
        var writer = new BundleWriter(this, archetype, row);
        bundle.Write(ref writer);
        foreach (var type in info.SparseTags)
            AddStored(entity, ref slot, type, ref Unsafe.NullRef<byte>());
        foreach (var type in info.Singletons)
            ClaimSingleton(entity, type);
        if (Tracking)
            archetype.StampAddedAll(row, version);
        if (observers.AnyAdd || changes.WantsAny(SpawnReports))
        {
            foreach (var type in info.SignatureTypes)
            {
                if (!observers.AnyAdd && !changes.Wants(type, SpawnReports))
                    continue;
                var column = archetype.ColumnSlot(type);
                Report(
                    ComponentChange.Added,
                    entity,
                    type,
                    default,
                    ref Unsafe.NullRef<byte>(),
                    ref column >= 0 ? ref archetype.RowRef(column, row) : ref Unsafe.NullRef<byte>()
                );
            }
        }
        foreach (var type in info.IndexedTypes)
            RefreshIndexes(entity, type);
        ThrowIndexConflicts();
        return entity;
    }

    internal BundleInfo BundleFor<TBundle>()
        where TBundle : struct, IBundle
    {
        var id = BundleSlot<TBundle>.Id;
        while (bundles.Count <= id)
            bundles.Add(null);
        if (bundles[id] is { } known)
            return known;

        var types = new List<int>();
        var shape = new BundleShape(types);
        TBundle.Describe(ref shape);
        foreach (var type in types)
        {
            if (!registry.Contains(type))
                throw new InvalidOperationException(
                    $"Bundle {typeof(TBundle).Name} holds a type outside this world's registry."
                );
            foreach (var required in registry[type].Requires)
            {
                if (!types.Contains(required))
                    throw new InvalidOperationException(
                        $"Bundle {typeof(TBundle).Name}: {registry[type].Name} requires {registry[required].Name}."
                    );
            }
        }
        var info = new BundleInfo(
            [
                .. types.Where(t =>
                    layout.Class[t] is StorageClass.Column or StorageClass.SignatureTag
                ),
            ],
            [.. types.Where(t => layout.Class[t] == StorageClass.SparseTag)],
            [.. types.Where(t => layout.SingletonSlot[t] >= 0)],
            [.. types.Where(t => layout.IndexBase[t] >= 0)]
        );
        bundles[id] = info;
        return info;
    }

    /// <summary>Marks the entity pending destroy: still readable by handle, excluded from queries by default, finalised by the next playback
    /// that finalises destroys. Inside a query walk the mark waits until the walk closes; for an entity an observer created, or while it has
    /// changes waiting, it waits for the next playback, or for the dispose of the entity's open <see cref="EntityBatch"/>; the target policy
    /// is checked then.</summary>
    /// <exception cref="TargetPolicyException">The entity, a descendant, or a source its destroy cascades to is the target of a
    /// <see cref="TargetPolicy.Throw"/> relation whose source is live and is not destroyed with it.</exception>
    [Effect(Effects.Structural | Effects.Mutates)]
    public void Destroy(
        Entity entity,
        LifecycleReason reason = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        DestroyEntity(entity, reason);
        VerifyInvariants();
    }

    void DestroyEntity(Entity entity, LifecycleReason reason)
    {
        CheckWritable();
        Structural();
        if (!allocator.IsCurrent(entity))
            return;
        ref var slot = ref allocator.Slots[entity.Index];
        // Guarded at playback, once the batch's reparents have landed.
        if (RecordsLifecycle(slot))
        {
            Stage(CommandKind.Destroy, entity, reason: reason);
            return;
        }
        if (OwnsDestroy(slot))
            return;
        CheckTargetPolicy(entity);
        MarkDestroyed(entity, ref slot, reason);
    }

    static bool OwnsDestroy(in EntitySlot slot) => slot.State == LifecycleState.PendingDestroy;

    // A recorded mark is judged against target policies once its batch has landed, and may yet be spared.
    void MarkDestroyed(
        Entity entity,
        ref EntitySlot slot,
        LifecycleReason reason,
        bool recorded = false,
        bool tag = true
    )
    {
        slot.State = LifecycleState.PendingDestroy;
        slot.DestroyReason = reason;
        slot.Archetype?.SetPendingDestroy(slot.Row, true);
        hierarchy.RaiseAncestors(entity, version);
        if (!slot.DestroyQueued)
        {
            slot.DestroyQueued = true;
            pendingDestroys.Add(entity);
        }
        if (recorded)
        {
            destroyedRoots.Add(entity);
            provisional.Add(entity);
        }
        if (tag && attributed is { } log)
            log.TagDestroy(entity.Index, log.Current(currentSystem));
        RefreshAllIndexes(entity);
        if (hierarchy.HasChildren(entity))
            RefreshChildren(entity);
    }

    /// <summary>Takes the entity out of pending destroy, and returns false if it was not pending destroy. An entity pending only through
    /// an ancestor is spared by unparenting it. Inside a query walk the cancel waits until the walk closes, and for an entity an observer
    /// created, or while it has changes waiting, until the next playback; it then returns whether reads see the entity dying, itself or
    /// through an ancestor. A destroy observer cannot rescue what a finalising playback is releasing: the cancel does nothing and returns
    /// false.</summary>
    /// <exception cref="TargetPolicyException">The spared entity or a descendant spared with it is the source of a
    /// <see cref="TargetPolicy.Throw"/> edge onto a dying target; nothing changed.</exception>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public bool CancelDestroy(
        Entity entity,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        CheckWritable();
        Structural();
        if (!allocator.IsCurrent(entity))
            return false;
        ref var slot = ref allocator.Slots[entity.Index];
        if (planFrozen && !IsLive(entity))
            return false;
        if (RecordsLifecycle(slot))
            return StageCancel(entity);
        if (!ApplyCancel(entity, ref slot))
            return false;
        VerifyInvariants();
        ThrowIndexConflicts();
        return true;
    }

    // Both routes: an entity pending itself is spared, one pending only through an ancestor leaves it.
    bool ApplyCancel(Entity entity, ref EntitySlot slot)
    {
        if (!OwnsDestroy(slot))
        {
            if (slot.State != LifecycleState.Alive || !UnderPendingDestroy(entity))
                return false;
            Reparent(entity, default, -1, default);
            return true;
        }
        if (!UnderPendingDestroy(entity) && RevivalFault(entity) is { } fault)
            throw fault;
        Spare(entity, ref slot);
        return true;
    }

    internal void Spare(Entity entity, ref EntitySlot slot)
    {
        slot.State = LifecycleState.Alive;
        slot.DestroyReason = default;
        attributed?.TagDestroy(entity.Index, default);
        slot.Archetype?.SetPendingDestroy(slot.Row, false);
        hierarchy.RaiseAncestors(entity, version);
        RefreshAllIndexes(entity);
        if (hierarchy.HasChildren(entity))
            RefreshChildren(entity);
        LogSpared(entity);
    }

    internal bool AwayAsRecorded(Entity from, bool dying, bool detached)
    {
        for (var node = from; !node.IsDefault; node = ParentAsRecorded(node))
        {
            ref readonly var slot = ref allocator.Slots[node.Index];
            if (
                (dying && overlay.Marked(node.Index, PendingOverlay.Destroying, OwnsDestroy(slot)))
                || (
                    detached
                    && overlay.Marked(node.Index, PendingOverlay.Detaching, slot.DetachedSelf)
                )
            )
                return true;
        }
        return false;
    }

    /// <summary>Moves the entity and its descendants into the detached partition, out of queries and systems but still reachable by
    /// handle; each moved entity is logged <see cref="LogKind.Detached"/> with <paramref name="reason"/>. Relation edges and parent links
    /// stay. An entity stays detached while it or any ancestor is; a descendant parented later joins the partition too. Inside a query walk
    /// the move waits until the walk closes, and inside an observer until the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist, or the move collides in a unique index.</exception>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public void Detach(
        Entity entity,
        LifecycleReason reason = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        SetPartition(entity, true, reason);
    }

    /// <summary>Undoes this entity's own <see cref="Detach"/>: it and its descendants return where queries and systems see them, except
    /// those that detached themselves or still have a detached ancestor; no-op when the entity did not detach itself. Inside a query walk
    /// the move waits until the walk closes, and inside an observer until the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist, or the move collides in a unique index.</exception>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public void Reattach(
        Entity entity,
        LifecycleReason reason = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        SetPartition(entity, false, reason);
    }

    void SetPartition(Entity entity, bool detached, LifecycleReason reason)
    {
        CheckWritable();
        Structural();
        ref var slot = ref Slot(entity);
        if (RecordsStructure(slot))
        {
            Stage(detached ? CommandKind.Detach : CommandKind.Reattach, entity, reason: reason);
            return;
        }
        if (slot.DetachedSelf == detached)
            return;
        slot.DetachedSelf = detached;
        RefreshPartitions(entity, reason);
        VerifyInvariants();
        ThrowIndexConflicts();
    }

    internal bool partitionsUsed;
    readonly List<(Entity Entity, bool Inherited)> partitionWalk = [];

    internal bool InheritsDetach(Entity entity) =>
        hierarchy.ParentOf(entity) is { IsDefault: false } parent && ShouldDetach(parent);

    bool ShouldDetach(Entity entity)
    {
        ref var slot = ref allocator.Slots[entity.Index];
        return slot.Archetype is not null
            ? slot.Detached
            : slot.DetachedSelf || InheritsDetach(entity);
    }

    // Each entity's partition follows from its own flag and its ancestors', so a subtree whose root keeps its partition is already right.
    internal void RefreshPartitions(Entity root, LifecycleReason reason)
    {
        partitionWalk.Clear();
        partitionWalk.Add((root, InheritsDetach(root)));
        while (partitionWalk.Count > 0)
        {
            var (entity, inherited) = partitionWalk[^1];
            partitionWalk.RemoveAt(partitionWalk.Count - 1);
            ref var slot = ref allocator.Slots[entity.Index];
            var detached = inherited || slot.DetachedSelf;
            if (slot.Archetype is { } archetype)
            {
                if (archetype.Detached == detached)
                    continue;
                MoveTo(entity, ref slot, Partitioned(archetype, detached));
                hierarchy.RaiseAncestors(entity, version);
                var kind = detached ? LogKind.Detached : LogKind.Reattached;
                changes.Append(kind, entity, -1, reason);
                observers.QueueLifecycle(entity, kind, reason);
                RefreshAllIndexes(entity);
            }
            foreach (var child in hierarchy.ChildrenOf(entity))
                partitionWalk.Add((child, detached));
        }
    }

    int FinalizeDestroys()
    {
        if (pendingDestroys.Count == 0)
            return 0;
        var batch = pendingDestroys.Count;
        SpareViolatingRoots(pendingDestroys, batch);
        PlanRelease(batch);
        var errors = NotifyDestroying(CollectionsMarshal.AsSpan(finalising), null, freeze: true);
        foreach (var entity in finalising)
        {
            attributed?.BeginReplay(attributed.DestroyTag(entity.Index));
            Release(entity, ReleaseReason(entity), LogKind.Destroyed);
        }
        attributed?.EndReplay();
        var finalised = finalising.Count;
        VerifyReleased(finalising);
        for (var i = 0; i < batch; i++)
        {
            ref var slot = ref allocator.Current(pendingDestroys[i]);
            if (!Unsafe.IsNullRef(ref slot))
                slot.DestroyQueued = false;
        }
        pendingDestroys.RemoveRange(0, batch);
        foreach (var source in cascadeRoots)
        {
            ref var slot = ref allocator.Current(source);
            if (Unsafe.IsNullRef(ref slot) || OwnsDestroy(slot))
                continue;
            MarkDestroyed(source, ref slot, LifecycleReason.Cascade, tag: false);
        }
        finalising.Clear();
        cascadeRoots.Clear();
        if (errors is not null)
            observerFailures.AddRange(errors);
        return finalised;
    }

    // The sources a cascade destroys next are read now, before a release clears the edges that name them.
    void PlanRelease(int batch)
    {
        finalising.Clear();
        cascadeRoots.Clear();
        BeginDoomed();
        for (var i = 0; i < batch; i++)
        {
            var root = pendingDestroys[i];
            ref var slot = ref allocator.Current(root);
            if (Unsafe.IsNullRef(ref slot) || !OwnsDestroy(slot) || doomedWaves[root.Index] != 0)
                continue;
            var start = doomed.Count;
            DoomSubtree(root, 1);
            // Children first, so a subtree is released bottom-up and each parent is still whole while its children go.
            for (var d = doomed.Count - 1; d >= start; d--)
            {
                var member = doomed[d];
                finalising.Add(member);
                if (!OwnsDestroy(allocator.Slots[member.Index]))
                    attributed?.InheritDestroy(member.Index, root.Index);
            }
        }
        foreach (var member in finalising)
        {
            foreach (var store in relations)
            {
                if (store is not { Policy: TargetPolicy.DestroySource })
                    continue;
                foreach (var source in store.SourcesOf(member))
                {
                    if (doomedWaves[source.Index] != 0)
                        continue;
                    Doom(source, 2);
                    cascadeRoots.Add(source);
                    attributed?.InheritDestroy(source.Index, member.Index);
                }
            }
        }
        EndDoomed();
    }

    LifecycleReason ReleaseReason(Entity entity)
    {
        ref readonly var slot = ref allocator.Slots[entity.Index];
        return slot.State == LifecycleState.PendingDestroy
            ? slot.DestroyReason
            : LifecycleReason.Cascade;
    }

    internal List<Exception>? NotifyDestroying(
        ReadOnlySpan<Entity> leaving,
        LifecycleReason? reason,
        bool freeze,
        bool lastFirst = false
    )
    {
        if (!observers.AnyDestroy)
            return null;
        using var delivery = BeginDelivery();
        planFrozen = freeze;
        try
        {
            List<Exception>? errors = null;
            for (var n = 0; n < leaving.Length; n++)
            {
                var entity = leaving[lastFirst ? leaving.Length - 1 - n : n];
                observers.RunDestroy(
                    new ObserverContext(this, reason ?? ReleaseReason(entity)),
                    entity,
                    ref errors
                );
            }
            return errors;
        }
        finally
        {
            planFrozen = false;
        }
    }

    // A spare revives the sources an earlier root's check counted as dying, so the roots are checked again until none is spared.
    void SpareViolatingRoots(List<Entity> roots, int count)
    {
        if (!guardsTargets || !AnyThrowEdge())
            return;
        bool spared;
        do
        {
            spared = false;
            for (var i = 0; i < count; i++)
            {
                var root = roots[i];
                ref var slot = ref allocator.Current(root);
                if (
                    Unsafe.IsNullRef(ref slot)
                    || !OwnsDestroy(slot)
                    || SurvivorOf(root) is not { } error
                )
                    continue;
                commandFailures.Add(error);
                Spare(root, ref slot);
                spared = true;
            }
        } while (spared);
    }

    void Release(Entity entity, LifecycleReason reason, LogKind kind)
    {
        foreach (var store in relations)
            store?.ReleaseEntity(this, entity, reason);
        foreach (var store in rowStores)
            store?.ReleaseEntity(this, entity, reason);
        if (!hierarchy.IsEmpty)
            ReleaseHierarchy(entity, reason);
        ref var slot = ref allocator.Slots[entity.Index];
        var placed = slot.Archetype is not null;
        var held = slot.Pending ? null : slot.Archetype;
        if (slot.Archetype is { } archetype)
        {
            if (observers.AnyRemove || archetype.HasSingleton || archetype.OwnsBuffers)
                DepartRow(entity, archetype, slot.Row, reason);
            Vacate(archetype, slot.Row);
        }
        if (slot.SparseCount > 0)
            ReleaseSparse(entity, ref slot, reason);
        ReleaseIndexes(entity, held);
        if (placed)
            changes.Append(kind, entity, -1, reason);
        allocator.Free(entity.Index);
    }

    void DepartRow(Entity entity, Archetype archetype, int row, LifecycleReason reason)
    {
        var types = archetype.Types;
        for (var i = 0; i < types.Length; i++)
        {
            var type = types[i];
            if (WatchesRemoval(type))
            {
                ref readonly var slot = ref archetype.SlotAt(i);
                Departing(
                    entity,
                    type,
                    ref slot.Column >= 0
                        ? ref archetype.RowRef(slot.Column, row)
                        : ref Unsafe.NullRef<byte>(),
                    reason,
                    present: slot.Enable < 0 || archetype.IsEnabled(slot.Enable, row)
                );
            }
            ReleaseSingleton(entity, type);
        }
    }

    void ReleaseSparse(Entity entity, ref EntitySlot slot, LifecycleReason reason)
    {
        foreach (var storage in sparse)
        {
            if (slot.SparseCount == 0)
                return;
            if (storage is null || storage.SlotOf(entity.Index) is not (var at and >= 0))
                continue;
            Departing(entity, storage.Type, ref storage.RowRef(at), reason);
            ReleaseSingleton(entity, storage.Type);
            storage.Remove(entity.Index);
            slot.SparseCount--;
        }
        foreach (var tags in sparseTags)
        {
            if (slot.SparseCount == 0)
                return;
            if (tags is not null && tags.Contains(entity.Index))
            {
                Departing(entity, tags.Type, ref Unsafe.NullRef<byte>(), reason);
                ReleaseSingleton(entity, tags.Type);
                tags.Set(entity.Index, false);
                slot.SparseCount--;
            }
        }
    }
}
