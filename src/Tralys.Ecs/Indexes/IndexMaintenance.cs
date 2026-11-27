using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

internal struct IndexEdit
{
    public Entity Entity;
    public int Type;
    public int ChunkIndex;
    public int Sequence;
}

public sealed partial class World
{
    internal readonly IndexStore?[] indexStores;
    internal readonly bool hasIndexes;
    int indexBatch;
    internal int indexRefreshes;
    readonly List<IndexStore> contested = [];
    IndexEdit[] indexEdits = [];
    long indexClock;
    long indexAt;

    internal long IndexTick() => ++indexClock;

    // Playback pins the position of the record it applies, so a unique key goes to the write the immediate calls would have made last.
    internal long NextIndexPosition() => indexAt != 0 ? indexAt : IndexTick();

    internal UniqueIndex<TKey> UniqueIndexOf<TComponent, TKey>(int ordinal)
        where TComponent : struct, IComponent
        where TKey : notnull => new(StoreOf<TComponent, UniqueStore<TKey>>(ordinal), false);

    internal MultiIndex<TKey> MultiIndexOf<TComponent, TKey>(int ordinal)
        where TComponent : struct, IComponent
        where TKey : notnull => new(StoreOf<TComponent, MultiStore<TKey>>(ordinal), false);

    internal OrderedIndex<TKey> OrderedIndexOf<TComponent, TKey>(int ordinal)
        where TComponent : struct, IComponent
        where TKey : notnull => new(StoreOf<TComponent, OrderedStore<TKey>>(ordinal), false);

    TStore? StoreOf<TComponent, TStore>(int ordinal)
        where TComponent : struct, IComponent
        where TStore : IndexStore
    {
        var type = registry.IndexOf<TComponent>();
        var slot = layout.IndexBase[type] + ordinal;
        if (
            layout.IndexBase[type] < 0
            || ordinal < 0
            || slot >= indexStores.Length
            || layout.IndexSlotType[slot] != type
        )
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                $"{typeof(TComponent).Name} has no index {ordinal}."
            );
        return indexStores[slot] switch
        {
            null => null,
            TStore store => store,
            _ => throw new InvalidOperationException(
                $"Index {ordinal} of {typeof(TComponent).Name} is a {registry[type].Indexes[ordinal].Kind} index."
            ),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RefreshIndexes(Entity entity, int type)
    {
        if (!hasIndexes)
            return;
        var first = layout.IndexBase[type];
        if (first >= 0)
            RefreshSlots(entity, type, first);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RefreshChangedIndexes<T>(Entity entity, int type, in T before, in T after)
        where T : struct
    {
        if (!hasIndexes)
            return;
        var first = layout.IndexBase[type];
        if (first < 0)
            return;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            RefreshSlots(entity, type, first);
        else
            RefreshMoved(
                entity,
                type,
                first,
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in before)),
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in after))
            );
    }

    void RefreshMoved(Entity entity, int type, int first, ref byte before, ref byte after)
    {
        var slotTypes = layout.IndexSlotType;
        // A pending entity's reads go through the overlay, which can hold a newer value; a disabled value is not indexed.
        var written =
            !allocator.Slots[entity.Index].Pending
            && (!layout.Enableable[type] || IsPresent(entity, type));
        for (var slot = first; slot < slotTypes.Length && slotTypes[slot] == type; slot++)
        {
            if (
                (indexStores[slot] is not { } store || store.KeyChanged(ref before, ref after))
                && StoreFor(slot, type, entity) is { } refreshed
            )
            {
                indexRefreshes++;
                if (written)
                    refreshed.Refresh(entity, ref after);
                else
                    refreshed.Refresh(entity);
            }
        }
    }

    bool AnyKeyChanged(int type, ref byte before, ref byte after)
    {
        var slotTypes = layout.IndexSlotType;
        for (
            var slot = layout.IndexBase[type];
            slot < slotTypes.Length && slotTypes[slot] == type;
            slot++
        )
        {
            if (indexStores[slot] is not { } store || store.KeyChanged(ref before, ref after))
                return true;
        }
        return false;
    }

    void RefreshSlots(Entity entity, int type, int first)
    {
        var slotTypes = layout.IndexSlotType;
        for (var slot = first; slot < slotTypes.Length && slotTypes[slot] == type; slot++)
            RefreshSlot(slot, type, entity);
    }

    void RefreshSlot(int slot, int type, Entity entity)
    {
        if (StoreFor(slot, type, entity) is not { } store)
            return;
        indexRefreshes++;
        store.Refresh(entity);
    }

    IndexStore? StoreFor(int slot, int type, Entity entity)
    {
        if (indexStores[slot] is { } store)
            return store;
        if (IndexLane(entity) < 0 || !IsPresent(entity, type))
            return null;
        var declaration = registry[type].Indexes[slot - layout.IndexBase[type]];
        return indexStores[slot] = declaration.Factory.Invoke(this, type, declaration);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RefreshAllIndexes(Entity entity)
    {
        if (hasIndexes)
            RefreshEveryIndex(entity);
    }

    // A pending entity may hold indexed values only in the overlay, which its archetype does not list.
    void RefreshEveryIndex(Entity entity)
    {
        ref var slot = ref allocator.Slots[entity.Index];
        if (slot.Pending || slot.Archetype is not { } archetype)
        {
            foreach (var type in layout.IndexedTypes)
                RefreshSlots(entity, type, layout.IndexBase[type]);
            return;
        }
        foreach (var type in archetype.Types)
        {
            var first = layout.IndexBase[type];
            if (first >= 0)
                RefreshSlots(entity, type, first);
        }
        foreach (var type in layout.SparseIndexedTypes)
            RefreshSlots(entity, type, layout.IndexBase[type]);
    }

    internal void RefreshParentIndexes(Entity entity)
    {
        foreach (var slot in layout.PerParentSlots)
            RefreshSlot(slot, layout.IndexSlotType[slot], entity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReleaseIndexes(Entity entity, Archetype? held)
    {
        if (hasIndexes)
            RemoveFromIndexes(entity, held);
    }

    // Without the archetype the entity held, overlay values may be indexed under any type.
    void RemoveFromIndexes(Entity entity, Archetype? held)
    {
        if (held is null)
        {
            foreach (var store in indexStores)
                store?.Remove(entity);
            return;
        }
        foreach (var type in held.Types)
            RemoveSlots(entity, type);
        foreach (var type in layout.SparseIndexedTypes)
            RemoveSlots(entity, type);
    }

    void RemoveSlots(Entity entity, int type)
    {
        var slotTypes = layout.IndexSlotType;
        var first = layout.IndexBase[type];
        if (first < 0)
            return;
        for (var slot = first; slot < slotTypes.Length && slotTypes[slot] == type; slot++)
            indexStores[slot]?.Remove(entity);
    }

    internal bool UnderPendingDestroy(Entity entity)
    {
        ref var slot = ref allocator.Slots[entity.Index];
        if (slot.Archetype is { } archetype)
            return archetype.HasAncestor(0, slot.Row);
        for (var up = hierarchy.ParentOf(entity); !up.IsDefault; up = hierarchy.ParentOf(up))
        {
            ref var above = ref allocator.Slots[up.Index];
            if (above.State == LifecycleState.PendingDestroy)
                return true;
            if (above.Archetype is { } placed)
                return placed.HasAncestor(0, above.Row);
        }
        return false;
    }

    internal int IndexLane(Entity entity)
    {
        ref var slot = ref allocator.Current(entity);
        if (
            Unsafe.IsNullRef(ref slot)
            || slot.State is not (LifecycleState.Alive or LifecycleState.Reserved)
        )
            return -1;
        if (UnderPendingDestroy(entity))
            return -1;
        return slot.Detached ? 1 : 0;
    }

    internal void IndexWritten<T>(
        Entity entity,
        int type,
        CommandBuffer? buffer,
        in T before,
        in T after
    )
        where T : struct
    {
        if (buffer is null)
            RefreshChangedIndexes(entity, type, before, after);
        else if (
            RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            || AnyKeyChanged(
                type,
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in before)),
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in after))
            )
        )
            buffer.RecordIndexEdit(entity, type);
    }

    internal void Contested(IndexStore store) => contested.Add(store);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ThrowIndexConflicts()
    {
        if (hasIndexes && contested.Count > 0 && indexBatch == 0)
            ThrowContested();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void ThrowContested()
    {
        if (TakeConflicts() is { } conflict)
            throw conflict;
    }

    IndexConflictException? TakeConflicts()
    {
        var found = new List<IndexConflict>();
        foreach (var store in contested)
            store.TakeConflicts(found);
        contested.Clear();
        return found.Count == 0 ? null : new IndexConflictException(found);
    }

    internal void BeginIndexBatch() => indexBatch++;

    internal IndexConflictException? EndIndexBatch() =>
        --indexBatch > 0 || contested.Count == 0 ? null : TakeConflicts();

    internal IndexConflictException? ApplyIndexEdits(ReadOnlySpan<CommandBuffer> buffers)
    {
        var total = 0;
        foreach (var buffer in buffers)
            total += buffer.indexEditCount;
        if (total == 0)
            return null;
        if (indexEdits.Length < total)
            indexEdits = new IndexEdit[Math.Max(total, indexEdits.Length * 2)];
        var at = 0;
        foreach (var buffer in buffers)
        {
            if (buffer.indexEditCount == 0)
                continue;
            Array.Copy(buffer.indexEdits, 0, indexEdits, at, buffer.indexEditCount);
            at += buffer.indexEditCount;
            buffer.indexEditCount = 0;
        }
        var edits = indexEdits.AsSpan(0, total);
        edits.Sort(
            static (a, b) =>
                a.ChunkIndex != b.ChunkIndex
                    ? a.ChunkIndex.CompareTo(b.ChunkIndex)
                    : a.Sequence.CompareTo(b.Sequence)
        );
        BeginIndexBatch();
        IndexConflictException? conflict;
        try
        {
            foreach (ref readonly var edit in edits)
                RefreshIndexes(edit.Entity, edit.Type);
        }
        finally
        {
            conflict = EndIndexBatch();
        }
        return conflict;
    }

    void ClearIndexes()
    {
        foreach (var store in indexStores)
            store?.Clear();
        contested.Clear();
    }
}
