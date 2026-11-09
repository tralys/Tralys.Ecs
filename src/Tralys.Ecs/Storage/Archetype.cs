using System.Numerics;
using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

internal struct RowStamp
{
    public ulong Changed;
    public ulong Added;
}

internal sealed record ArchetypeState(
    int Count,
    Entity[] Entities,
    Array[] Columns,
    ulong[] EnableWords,
    ulong[] DestroyWords,
    ulong[] AncestorWords,
    RowStamp[] Stamps,
    ulong[] MaxChanged
);

internal sealed class MoveMap(
    Archetype target,
    int[] columns,
    int[] enables,
    int[] tracked,
    int[] freshTracked
)
{
    public readonly Archetype Target = target;

    public readonly int[] Columns = columns;
    public readonly int[] Enables = enables;
    public readonly int[] Tracked = tracked;
    public readonly int[] FreshTracked = freshTracked;
}

internal sealed class Archetype
{
    public readonly int Id;
    public readonly ArchetypeSignature Signature;
    public readonly int[] Types;
    public readonly int[] ColumnTypes;
    public readonly ColumnOps[] ColumnOps;
    public readonly int[] ColumnSizes;
    public readonly int[] EnableTypes;
    public readonly int[] TrackedTypes;
    public readonly bool HasSingleton;
    public readonly bool OwnsBuffers;
    public readonly TypeMap Map;

    public readonly int ChunkCapacity;
    readonly int chunkShift;
    readonly int enableCount;
    readonly int trackedCount;

    public readonly Array[] Columns;
    public Entity[] Entities = [];

    // A set bit means enabled; bits past Count stay clear, so an all-set word never covers a dead row.
    public ulong[] EnableWords = [];

    public ulong[] DestroyWords = [];

    // Per inherited kind: the bit an entity inherits from its ancestors (their pending destroy or inherited tag), never its own.
    public ulong[] AncestorWords = [];
    readonly int inheritedCount;
    readonly HierarchyStore hierarchy;

    readonly World? propagator;
    readonly bool[] propagates;

    // Stamps of rows past Count are stale; whoever fills a row writes every stamp of it.
    RowStamp[] stamps = [];

    public ulong[] MaxChanged = [];

    public int Words;
    public int Count;

    public readonly Dictionary<int, Archetype> AddEdges = [];
    public readonly Dictionary<int, Archetype> RemoveEdges = [];
    public Archetype? PartitionEdge;
    MoveMap? lastMove;
    Dictionary<int, MoveMap>? moves;

    public Archetype(
        int id,
        ArchetypeSignature signature,
        Registry registry,
        int chunkCapacity,
        World world
    )
    {
        var layout = registry.Layout;
        inheritedCount = layout.InheritedCount;
        hierarchy = world.hierarchy;
        Id = id;
        Signature = signature;
        ChunkCapacity = chunkCapacity;
        chunkShift = BitOperations.Log2((uint)chunkCapacity);
        Types = signature.Types();
        var slots = new TypeSlot[Types.Length];
        var columns = new List<int>();
        var enables = new List<int>();
        var tracked = new List<int>();
        for (var i = 0; i < Types.Length; i++)
        {
            var type = Types[i];
            var descriptor = registry[type];
            var column = descriptor.HasColumn ? columns.Count : -1;
            var enable = descriptor.IsEnableable ? enables.Count : -1;
            var track =
                descriptor.IsTracked && (descriptor.HasColumn || descriptor.IsEnableable)
                    ? tracked.Count
                    : -1;
            if (column >= 0)
                columns.Add(type);
            if (enable >= 0)
                enables.Add(type);
            if (track >= 0)
                tracked.Add(type);
            HasSingleton |= (descriptor.Traits & ComponentTraits.Singleton) != 0;
            OwnsBuffers |= layout.OwnsBuffers[type];
            slots[i] = new TypeSlot(column, enable, track);
        }
        Map = new TypeMap(signature.Words, slots);
        ColumnTypes = [.. columns];
        ColumnOps = [.. columns.Select(t => registry[t].Column)];
        ColumnSizes = [.. ColumnOps.Select(o => o.Size)];
        EnableTypes = [.. enables];
        TrackedTypes = [.. tracked];
        enableCount = EnableTypes.Length;
        trackedCount = TrackedTypes.Length;
        propagates =
        [
            .. TrackedTypes.Select(t =>
                layout.PropagatesToAncestors[t] || layout.PropagatesToSources[t]
            ),
        ];
        if (Array.IndexOf(propagates, true) >= 0)
            propagator = world;
        Columns = new Array[ColumnOps.Length];
        for (var c = 0; c < Columns.Length; c++)
            Columns[c] = ColumnOps[c].Create(0);
    }

    public bool Detached => Signature.Detached;

    public int Capacity => Entities.Length;

    public int ChunkCount => (Count + ChunkCapacity - 1) >> chunkShift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int IndexOf(int type) => Map.IndexOf(type);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TypeSlot SlotAt(int index) => ref Map.SlotAt(index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ColumnSlot(int type) => IndexOf(type) is var i and >= 0 ? SlotAt(i).Column : -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int EnableSlot(int type) => IndexOf(type) is var i and >= 0 ? SlotAt(i).Enable : -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int TrackedSlot(int type) => IndexOf(type) is var i and >= 0 ? SlotAt(i).Tracked : -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T[] Values<T>(int column) => Unsafe.As<T[]>(Columns[column]);

    public ref byte RowRef(int column, int row) =>
        ref RowBytes.At(Columns[column], row, ColumnSizes[column]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled(int enableSlot, int row) =>
        (EnableWords[(enableSlot * Words) + (row >> 6)] & (1UL << row)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetEnabled(int enableSlot, int row, bool enabled)
    {
        ref var word = ref EnableWords[(enableSlot * Words) + (row >> 6)];
        word = enabled ? word | (1UL << row) : word & ~(1UL << row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsPendingDestroy(int row) => (DestroyWords[row >> 6] & (1UL << row)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPendingDestroy(int row, bool pending)
    {
        ref var word = ref DestroyWords[row >> 6];
        word = pending ? word | (1UL << row) : word & ~(1UL << row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAncestor(int kind, int row) =>
        (AncestorWords[(kind * Words) + (row >> 6)] & (1UL << row)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetAncestor(int kind, int row, bool on)
    {
        ref var word = ref AncestorWords[(kind * Words) + (row >> 6)];
        word = on ? word | (1UL << row) : word & ~(1UL << row);
    }

    public void CopyAncestors(int row, Archetype source, int sourceRow)
    {
        for (var k = 0; k < inheritedCount; k++)
            SetAncestor(k, row, source.HasAncestor(k, sourceRow));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ChangedAt(int trackedSlot, int row) =>
        stamps[(row * trackedCount) + trackedSlot].Changed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong AddedAt(int trackedSlot, int row) =>
        stamps[(row * trackedCount) + trackedSlot].Added;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong MaxChangedIn(int chunk, int trackedSlot) =>
        MaxChanged[(chunk * trackedCount) + trackedSlot];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void RaiseMax(int trackedSlot, int row, ulong version)
    {
        ref var max = ref MaxChanged[((row >> chunkShift) * trackedCount) + trackedSlot];
        if (version > max)
            max = version;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Stamp(int trackedSlot, int row, ulong version)
    {
        StampOnly(trackedSlot, row, version);
        if (propagator is not null && propagates[trackedSlot])
            propagator.Propagate(Entities[row], TrackedTypes[trackedSlot], version);
    }

    // The caller has ruled out propagation; a call here would make inlined row loops spill.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampOnly(int trackedSlot, int row, ulong version)
    {
        stamps[(row * trackedCount) + trackedSlot].Changed = version;
        RaiseMax(trackedSlot, row, version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampAdded(int trackedSlot, int row, ulong version)
    {
        stamps[(row * trackedCount) + trackedSlot] = new RowStamp
        {
            Changed = version,
            Added = version,
        };
        RaiseMax(trackedSlot, row, version);
        if (propagator is not null && propagates[trackedSlot])
            propagator.Propagate(Entities[row], TrackedTypes[trackedSlot], version);
    }

    // Propagation reads no stamps, so it runs after the loop and keeps a call out of the loop body.
    public void StampAddedAll(int row, ulong version)
    {
        for (var t = 0; t < trackedCount; t++)
        {
            stamps[(row * trackedCount) + t] = new RowStamp { Changed = version, Added = version };
            RaiseMax(t, row, version);
        }
        if (propagator is null)
            return;
        for (var t = 0; t < trackedCount; t++)
        {
            if (propagates[t])
                propagator.Propagate(Entities[row], TrackedTypes[t], version);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyStamps(
        int trackedSlot,
        int row,
        Archetype source,
        int sourceSlot,
        int sourceRow
    )
    {
        ref readonly var stamp = ref source.stamps[(sourceRow * source.trackedCount) + sourceSlot];
        stamps[(row * trackedCount) + trackedSlot] = stamp;
        RaiseMax(trackedSlot, row, stamp.Changed);
    }

    public void ClearStamps(int row) => stamps.AsSpan(row * trackedCount, trackedCount).Clear();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearStamps(int trackedSlot, int row) =>
        stamps[(row * trackedCount) + trackedSlot] = default;

    public MoveMap MapTo(Archetype target)
    {
        if (lastMove?.Target == target)
            return lastMove;
        moves ??= [];
        if (!moves.TryGetValue(target.Id, out var map))
        {
            map = BuildMap(target);
            moves.Add(target.Id, map);
        }
        return lastMove = map;
    }

    MoveMap BuildMap(Archetype target)
    {
        var columns = new List<int>();
        var enables = new List<int>();
        var tracked = new List<int>();
        var fresh = new List<int>();
        for (var i = 0; i < target.Types.Length; i++)
        {
            var at = IndexOf(target.Types[i]);
            if (at < 0)
            {
                if (target.SlotAt(i).Tracked >= 0)
                    fresh.Add(target.SlotAt(i).Tracked);
                continue;
            }
            ref readonly var from = ref SlotAt(at);
            ref readonly var to = ref target.SlotAt(i);
            Pair(columns, from.Column, to.Column);
            Pair(enables, from.Enable, to.Enable);
            Pair(tracked, from.Tracked, to.Tracked);
        }
        return new MoveMap(target, [.. columns], [.. enables], [.. tracked], [.. fresh]);

        static void Pair(List<int> pairs, int from, int to)
        {
            if (to < 0)
                return;
            pairs.Add(from);
            pairs.Add(to);
        }
    }

    public int AllocateRow(Entity entity)
    {
        var row = Count;
        if (row == Capacity)
            Grow();
        Entities[row] = entity;
        for (var e = 0; e < enableCount; e++)
            SetEnabled(e, row, true);
        Count++;
        return row;
    }

    void Grow()
    {
        var capacity = Capacity == 0 ? ChunkCapacity : Capacity * 2;
        var words = (capacity + 63) >> 6;
        for (var c = 0; c < Columns.Length; c++)
            Columns[c] = ColumnOps[c].Resize(Columns[c], capacity);
        Array.Resize(ref Entities, capacity);
        if (enableCount > 0)
        {
            var grown = new ulong[enableCount * words];
            for (var e = 0; e < enableCount; e++)
                Array.Copy(EnableWords, e * Words, grown, e * words, Words);
            EnableWords = grown;
        }
        Array.Resize(ref DestroyWords, words);
        var ancestors = new ulong[inheritedCount * words];
        for (var k = 0; k < inheritedCount; k++)
            Array.Copy(AncestorWords, k * Words, ancestors, k * words, Words);
        AncestorWords = ancestors;
        Array.Resize(ref stamps, capacity * trackedCount);
        Array.Resize(ref MaxChanged, (capacity >> chunkShift) * trackedCount);
        Words = words;
    }

    public Entity? RemoveRow(int row)
    {
        var last = --Count;
        Entity? moved = null;
        if (row != last)
        {
            moved = Entities[last];
            MoveRow(last, row);
        }
        else
            ClearRow(last);
        if ((Count & (ChunkCapacity - 1)) == 0)
            MaxChanged.AsSpan((Count >> chunkShift) * trackedCount, trackedCount).Clear();
        return moved;
    }

    void MoveRow(int from, int to)
    {
        Entities[to] = Entities[from];
        for (var c = 0; c < Columns.Length; c++)
        {
            if (ColumnOps[c].Managed)
                ColumnOps[c].MoveManaged(Columns[c], from, Columns[c], to);
            else
                RowBytes.Copy(Columns[c], from, Columns[c], to, ColumnSizes[c]);
        }
        for (var e = 0; e < enableCount; e++)
            SetEnabled(e, to, IsEnabled(e, from));
        SetPendingDestroy(to, IsPendingDestroy(from));
        if (hierarchy.EverUsed)
            CopyAncestors(to, this, from);
        for (var t = 0; t < trackedCount; t++)
            CopyStamps(t, to, this, t, from);
        ClearMeta(from);
    }

    void ClearRow(int row)
    {
        for (var c = 0; c < Columns.Length; c++)
        {
            if (ColumnOps[c].Managed)
                ColumnOps[c].ClearManaged(Columns[c], row);
        }
        ClearMeta(row);
    }

    void ClearMeta(int row)
    {
        Entities[row] = default;
        for (var e = 0; e < enableCount; e++)
            SetEnabled(e, row, false);
        SetPendingDestroy(row, false);
        if (!hierarchy.EverUsed)
            return;
        for (var k = 0; k < inheritedCount; k++)
            SetAncestor(k, row, false);
    }

    public void Release()
    {
        for (var c = 0; c < Columns.Length; c++)
        {
            if (ColumnOps[c].Managed)
                Array.Clear(Columns[c], 0, Count);
        }
        Array.Clear(Entities, 0, Count);
        Array.Clear(EnableWords);
        Array.Clear(DestroyWords);
        Array.Clear(AncestorWords);
        Array.Clear(MaxChanged);
        Count = 0;
    }

    public ArchetypeState Save()
    {
        var used = (Count + 63) >> 6;
        var columns = new Array[Columns.Length];
        for (var c = 0; c < Columns.Length; c++)
        {
            columns[c] = ColumnOps[c].Create(Count);
            Array.Copy(Columns[c], columns[c], Count);
        }
        return new ArchetypeState(
            Count,
            Entities.AsSpan(0, Count).ToArray(),
            columns,
            Strided(EnableWords, enableCount, used),
            DestroyWords.AsSpan(0, used).ToArray(),
            Strided(AncestorWords, inheritedCount, used),
            stamps.AsSpan(0, Count * trackedCount).ToArray(),
            MaxChanged.AsSpan(0, ChunkCount * trackedCount).ToArray()
        );
    }

    ulong[] Strided(ulong[] words, int slots, int used)
    {
        var packed = new ulong[slots * used];
        for (var s = 0; s < slots; s++)
            Array.Copy(words, s * Words, packed, s * used, used);
        return packed;
    }

    public void Load(ArchetypeState state)
    {
        Release();
        while (Capacity < state.Count)
            Grow();
        var used = (state.Count + 63) >> 6;
        for (var c = 0; c < Columns.Length; c++)
            Array.Copy(state.Columns[c], Columns[c], state.Count);
        state.Entities.CopyTo(Entities, 0);
        for (var e = 0; e < enableCount; e++)
            Array.Copy(state.EnableWords, e * used, EnableWords, e * Words, used);
        state.DestroyWords.CopyTo(DestroyWords, 0);
        for (var k = 0; k < inheritedCount; k++)
            Array.Copy(state.AncestorWords, k * used, AncestorWords, k * Words, used);
        state.Stamps.CopyTo(stamps, 0);
        state.MaxChanged.CopyTo(MaxChanged, 0);
        Count = state.Count;
    }

    public void ForgetFrom(int count)
    {
        Forget(AddEdges, count);
        Forget(RemoveEdges, count);
        if (PartitionEdge?.Id >= count)
            PartitionEdge = null;
        if (lastMove?.Target.Id >= count)
            lastMove = null;
        if (moves is null)
            return;
        foreach (var id in moves.Keys.ToArray())
        {
            if (id >= count)
                moves.Remove(id);
        }

        static void Forget(Dictionary<int, Archetype> edges, int count)
        {
            foreach (var (type, target) in edges.ToArray())
            {
                if (target.Id >= count)
                    edges.Remove(type);
            }
        }
    }

    public void TrimStorage()
    {
        for (var c = 0; c < Columns.Length; c++)
            Columns[c] = ColumnOps[c].Create(0);
        Entities = [];
        EnableWords = [];
        DestroyWords = [];
        AncestorWords = [];
        stamps = [];
        MaxChanged = [];
        Words = 0;
    }
}
