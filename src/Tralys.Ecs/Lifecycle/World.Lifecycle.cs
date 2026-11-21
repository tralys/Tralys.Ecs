using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    readonly List<Entity> pendingDestroys = [];
    readonly List<Entity> finalising = [];
    readonly List<(Entity Entity, LifecycleState State)> inheriting = [];
    readonly List<Entity> restored = [];
    byte[] gathered = [];
    const byte Gathered = 1;
    const byte Reached = 2;

    static readonly LogKinds SpawnReports = LogKinds.Of(LogKind.Added);

    readonly List<BundleInfo?> bundles = [];
    internal bool extracting;

    /// <summary>Creates an entity. Inside an observer it is reserved: usable by handle at once, matched by queries after playback.</summary>
    /// <exception cref="InvalidOperationException">A serial query body is running.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public Entity Create([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        At(file, line);
        CheckWritable();
        return NewEntity();
    }

    internal Entity NewEntity()
    {
        CheckStructural();
        if (iterationDepth > 0)
        {
            var reserved = allocator.Allocate(LifecycleState.Reserved);
            allocator.Slots[reserved.Index].Pending = true;
            stage.Record(CommandKind.Create, reserved);
            return reserved;
        }
        var entity = allocator.Allocate(LifecycleState.Alive);
        Place(entity, ref allocator.Slots[entity.Index], root);
        changes.Append(LogKind.Created, entity, -1, default);
        return entity;
    }

    /// <summary>Creates an entity with a whole bundle in one archetype move; inside an observer it is reserved, as <see cref="Create(string, int)"/>
    /// makes it.</summary>
    /// <exception cref="InvalidOperationException">A serial query body is running, or a singleton in the bundle is held.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public Entity Create<TBundle>(
        in TBundle bundle,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where TBundle : struct, IBundle
    {
        At(file, line);
        CheckWritable();
        CheckStructural();
        var info = BundleFor<TBundle>();
        foreach (var type in info.Singletons)
        {
            if (SingletonRival(default, layout.SingletonSlot[type]) is { IsDefault: false } holder)
                throw new InvalidOperationException(
                    SingletonConflict(type, holder, "a new entity")
                );
        }
        if (iterationDepth > 0)
        {
            var reserved = NewEntity();
            var recorder = new BundleWriter(stage, reserved);
            bundle.Write(ref recorder);
            foreach (var type in info.SignatureTypes)
            {
                if (layout.Class[type] == StorageClass.SignatureTag)
                {
                    stage.Record(CommandKind.Set, reserved, type, -1);
                    overlay.Set(reserved.Index, type, true, -1);
                }
            }
            foreach (var type in info.SparseTags)
            {
                stage.Record(CommandKind.Set, reserved, type, -1);
                overlay.Set(reserved.Index, type, true, -1);
            }
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
            AddSparseTag(entity, ref slot, type);
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
    /// that finalises destroys. For an entity an observer created, or while it has changes waiting, the mark itself waits for the next
    /// playback, or for the dispose of the entity's open <see cref="EntityBatch"/>, which checks the target policy then.</summary>
    /// <exception cref="TargetPolicyException">The entity, a descendant, or a source its destroy cascades to is the target of a
    /// <see cref="TargetPolicy.Throw"/> relation whose source is live and is not destroyed with it.</exception>
    /// <exception cref="InvalidOperationException">A serial query body is running.</exception>
    [Effect(Effects.Structural | Effects.Mutates)]
    public void Destroy(
        Entity entity,
        LifecycleReason reason = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        DestroyEntity(entity, reason, guardTargets: true);
    }

    internal void DestroyEntity(Entity entity, LifecycleReason reason, bool guardTargets)
    {
        CheckWritable();
        CheckStructural();
        if (!allocator.IsCurrent(entity))
            return;
        ref var slot = ref allocator.Slots[entity.Index];
        // Guarded at playback, once the batch's reparents have landed.
        if (DefersLifecycle(slot))
        {
            stage.Record(CommandKind.Destroy, entity, reason: reason);
            slot.Pending = true;
            return;
        }
        if (slot.State == LifecycleState.PendingDestroy && OwnsDestroy(slot))
            return;
        if (guardTargets)
            CheckTargetPolicy(entity);
        MarkPendingDestroy(entity, ref slot, reason);
        if (attributed is { } log)
            log.TagDestroy(entity.Index, log.Current(currentSystem));
        RefreshAllIndexes(entity);
        if (hierarchy.HasChildren(entity))
            RefreshChildren(entity);
    }

    static bool OwnsDestroy(in EntitySlot slot) =>
        slot.Archetype?.IsPendingDestroy(slot.Row) == true;

    void MarkPendingDestroy(Entity entity, ref EntitySlot slot, LifecycleReason reason)
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
    }

    /// <summary>Takes the entity out of pending destroy, and returns false if it was not pending destroy. An entity pending only through
    /// an ancestor is spared by unparenting it. For an entity an observer created, or while it has changes waiting, the cancel waits for
    /// the next playback and returns whether it spares a pending entity or overrides a recorded destroy.</summary>
    /// <exception cref="InvalidOperationException">A serial query body is running.</exception>
    [Effect(Effects.Structural | Effects.Mutates)]
    public bool CancelDestroy(
        Entity entity,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        CheckWritable();
        CheckStructural();
        if (!allocator.IsCurrent(entity))
            return false;
        ref var slot = ref allocator.Slots[entity.Index];
        if (DefersLifecycle(slot))
        {
            var spares =
                slot.State == LifecycleState.PendingDestroy
                || (slot.State == LifecycleState.Alive && UnderPendingDestroy(entity))
                || AncestorDestroyRecorded(entity);
            stage.Record(CommandKind.CancelDestroy, entity);
            slot.Pending = true;
            return CancelsRecordedDestroy(stage.records[stage.count - 1]) || spares;
        }
        if (slot.State != LifecycleState.PendingDestroy)
        {
            if (slot.State != LifecycleState.Alive || !UnderPendingDestroy(entity))
                return false;
            Reparent(entity, default, -1, default, guardTargets: false);
            return true;
        }
        Spare(entity, ref slot);
        ThrowIndexConflicts();
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

    bool AncestorDestroyRecorded(Entity entity)
    {
        for (var up = hierarchy.ParentOf(entity); !up.IsDefault; up = hierarchy.ParentOf(up))
        {
            for (var i = stage.count - 1; i >= 0; i--)
            {
                ref readonly var record = ref stage.records[i];
                if (record.Entity != up)
                    continue;
                if (record.Kind == CommandKind.Destroy)
                    return true;
                if (record.Kind == CommandKind.CancelDestroy)
                    break;
            }
        }
        return false;
    }

    bool CancelsRecordedDestroy(in CommandRecord cancel)
    {
        CommandRecord latest = default;
        var any = false;
        if (!LatestBefore(stage, cancel, ref latest, ref any))
            return false;
        foreach (var buffer in submitted)
        {
            if (!LatestBefore(buffer, cancel, ref latest, ref any))
                return false;
        }
        return any && latest.Kind == CommandKind.Destroy;
    }

    static bool LatestBefore(
        CommandBuffer buffer,
        in CommandRecord cancel,
        ref CommandRecord latest,
        ref bool any
    )
    {
        for (var i = 0; i < buffer.count; i++)
        {
            ref readonly var record = ref buffer.records[i];
            if (
                record.Entity != cancel.Entity
                || record.Kind is not (CommandKind.Destroy or CommandKind.CancelDestroy)
            )
                continue;
            var order = PlaybackOrder(record, cancel);
            if (order > 0 && record.Kind == CommandKind.Destroy)
                return false;
            if (order < 0 && (!any || PlaybackOrder(record, latest) > 0))
            {
                latest = record;
                any = true;
            }
        }
        return true;
    }

    /// <summary>Moves the entity and its descendants into the detached partition, out of queries and systems but still reachable by
    /// handle; each moved entity is logged <see cref="LogKind.Detached"/> with <paramref name="reason"/>. Relation edges and parent links
    /// stay. An entity stays detached while it or any ancestor is; a descendant parented later joins the partition too. Inside an observer
    /// the move defers to the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist, the move collides in a unique index, or a serial query body is
    /// running.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
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
    /// those that detached themselves or still have a detached ancestor; no-op when the entity did not detach itself. Inside an observer
    /// the move defers to the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist, the move collides in a unique index, or a serial query body is
    /// running.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
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
        CheckStructural();
        ref var slot = ref Slot(entity);
        partitionsUsed = true;
        if (Defers(slot))
        {
            stage.Record(
                detached ? CommandKind.Detach : CommandKind.Reattach,
                entity,
                reason: reason
            );
            slot.Pending = true;
            return;
        }
        if (slot.DetachedSelf == detached)
            return;
        slot.DetachedSelf = detached;
        RefreshPartitions(entity, reason);
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

    // Each pending root takes its whole subtree with it, children first; descendants not marked themselves take the root's reason.
    int FinalizeDestroys()
    {
        if (pendingDestroys.Count == 0)
            return 0;
        var batch = pendingDestroys.Count;
        if (guardsTargets && AnyThrowEdge())
            CheckFinalisable(batch);
        finalising.Clear();
        inheriting.Clear();
        if (gathered.Length < allocator.Capacity)
            Array.Resize(ref gathered, Math.Max(allocator.Capacity, gathered.Length * 2));
        for (var i = 0; i < batch; i++)
        {
            var root = pendingDestroys[i];
            ref var slot = ref allocator.Current(root);
            if (Unsafe.IsNullRef(ref slot))
                continue;
            if (slot.State != LifecycleState.PendingDestroy)
                slot.DestroyQueued = false;
            else if (gathered[root.Index] == 0)
                GatherSubtree(root, finalising);
        }
        try
        {
            if (observers.destroys.Length > 0)
                NotifyFinalising();
        }
        finally
        {
            foreach (var entity in finalising)
                gathered[entity.Index] = 0;
        }

        var finalised = 0;
        foreach (var entity in finalising)
        {
            ref var slot = ref allocator.Current(entity);
            if (Unsafe.IsNullRef(ref slot))
                continue;
            if (slot.State != LifecycleState.PendingDestroy)
            {
                slot.DestroyQueued = false;
                continue;
            }
            attributed?.BeginReplay(attributed.DestroyTag(entity.Index));
            Release(entity, slot.DestroyReason);
            finalised++;
        }
        attributed?.EndReplay();
        pendingDestroys.RemoveRange(0, batch);
        finalising.Clear();
        inheriting.Clear();
        return finalised;
    }

    void NotifyFinalising()
    {
        using var delivery = BeginDelivery();
        try
        {
            List<Exception>? errors = null;
            foreach (var entity in finalising)
            {
                var context = new ObserverContext(
                    this,
                    allocator.Slots[entity.Index].DestroyReason
                );
                foreach (var (_, observer) in observers.destroys)
                {
                    try
                    {
                        observer.OnDestroy(context, entity);
                    }
                    catch (Exception error)
                    {
                        (errors ??= []).Add(error);
                    }
                }
            }
            ObserverRegistry.Rethrow(errors);
            SpareRescuedDescendants();
            if (guardsTargets && AnyRescued() && AnyThrowEdge())
                CheckFinalising();
        }
        catch
        {
            RestoreInherited(spare: false);
            throw;
        }
    }

    bool AnyRescued()
    {
        foreach (var entity in finalising)
        {
            ref var slot = ref allocator.Current(entity);
            if (Unsafe.IsNullRef(ref slot) || slot.State != LifecycleState.PendingDestroy)
                return true;
        }
        return false;
    }

    // A descendant stays doomed only through a chain of this batch's pending entities up to one marked itself, as the hierarchy is now.
    void SpareRescuedDescendants()
    {
        if (inheriting.Count == 0)
            return;
        var count = 0;
        foreach (var entity in finalising)
        {
            ref var slot = ref allocator.Current(entity);
            if (
                Unsafe.IsNullRef(ref slot)
                || slot.State != LifecycleState.PendingDestroy
                || !OwnsDestroy(slot)
            )
                continue;
            gathered[entity.Index] = Reached;
            Push(ref count, entity);
        }
        while (count > 0)
        {
            foreach (var child in hierarchy.ChildrenOf(walk[--count]))
            {
                if (
                    child.Index >= gathered.Length
                    || gathered[child.Index] != Gathered
                    || allocator.Slots[child.Index].State != LifecycleState.PendingDestroy
                )
                    continue;
                gathered[child.Index] = Reached;
                Push(ref count, child);
            }
        }
        RestoreInherited(spare: true);
    }

    void RestoreInherited(bool spare)
    {
        restored.Clear();
        foreach (var (entity, state) in inheriting)
        {
            ref var slot = ref allocator.Current(entity);
            if (
                Unsafe.IsNullRef(ref slot)
                || slot.State != LifecycleState.PendingDestroy
                || OwnsDestroy(slot)
                || (spare && gathered[entity.Index] == Reached)
            )
                continue;
            slot.State = state;
            slot.DestroyReason = default;
            attributed?.TagDestroy(entity.Index, default);
            if (spare)
                changes.Append(LogKind.DestroyCancelled, entity, -1, default);
            restored.Add(entity);
        }
        foreach (var entity in restored)
            RefreshAllIndexes(entity);
        restored.Clear();
        if (!spare)
            inheriting.Clear();
    }

    void Release(Entity entity, LifecycleReason reason)
    {
        foreach (var store in relations)
            store?.ReleaseEntity(this, entity);
        foreach (var store in rowStores)
            store?.ReleaseEntity(this, entity, reason);
        if (!hierarchy.IsEmpty)
            ReleaseHierarchy(entity, reason);
        ref var slot = ref allocator.Slots[entity.Index];
        var placed = slot.Archetype is not null;
        var held = slot.Pending ? null : slot.Archetype;
        if (slot.Archetype is { } archetype)
        {
            if (
                coreHooks.Count > 0
                || observers.AnyRemove
                || archetype.HasSingleton
                || archetype.OwnsBuffers
            )
                DepartRow(entity, archetype, slot.Row, reason);
            Vacate(archetype, slot.Row);
        }
        if (slot.SparseCount > 0)
            ReleaseSparse(entity, ref slot, reason);
        ReleaseIndexes(entity, held);
        if (placed)
            changes.Append(extracting ? LogKind.Extracted : LogKind.Destroyed, entity, -1, reason);
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

    internal IterationScope BeginIteration()
    {
        iterationDepth++;
        walkDepth++;
        return new IterationScope(this, walk: true);
    }

    internal IterationScope BeginDelivery()
    {
        iterationDepth++;
        return new IterationScope(this, walk: false);
    }

    internal ref struct IterationScope(World world, bool walk)
    {
        World? world = world;

        public void Dispose()
        {
            if (world is null)
                return;
            if (world.iterationDepth > 0)
                world.iterationDepth--;
            if (walk && world.walkDepth > 0)
                world.walkDepth--;
            world = null;
        }
    }

    internal void ReplayAdds(int type, int recipient)
    {
        switch (layout.Class[type])
        {
            case StorageClass.Sparse:
                if (sparse[layout.SparseSlot[type]] is { } storage)
                {
                    for (var i = 0; i < storage.Count; i++)
                        observers.QueueReplay(
                            storage.Owners[i],
                            type,
                            ref storage.RowRef(i),
                            recipient
                        );
                }
                return;
            case StorageClass.SparseTag:
                if (sparseTags[layout.SparseTagSlot[type]] is { } tags)
                {
                    foreach (var holder in HoldersOf(tags))
                        observers.QueueReplay(holder, type, ref ZeroBytes.Value, recipient);
                }
                return;
        }
        foreach (var archetype in archetypes)
        {
            if (!archetype.Signature.Has(type))
                continue;
            var column = archetype.ColumnSlot(type);
            var enable = archetype.EnableSlot(type);
            for (var row = 0; row < archetype.Count; row++)
            {
                if (enable >= 0 && !archetype.IsEnabled(enable, row))
                    continue;
                observers.QueueReplay(
                    archetype.Entities[row],
                    type,
                    ref column >= 0 ? ref archetype.RowRef(column, row) : ref ZeroBytes.Value,
                    recipient
                );
            }
        }
    }

    IEnumerable<Entity> HoldersOf(SparseTagStorage tags)
    {
        foreach (var index in tags.Indices())
            yield return new Entity(index, allocator.Slots[index].Generation);
    }
}
