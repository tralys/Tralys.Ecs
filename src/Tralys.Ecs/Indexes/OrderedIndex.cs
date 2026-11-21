using System.Diagnostics.CodeAnalysis;

namespace Tralys.Ecs;

/// <summary>Entities ordered by key, such as deadlines. Pending-destroy entities are never listed; detached ones only through
/// <see cref="IncludeDetached"/>. Equal keys keep the order their entries took them.</summary>
public readonly ref struct OrderedIndex<TKey>
    where TKey : notnull
{
    readonly OrderedStore<TKey>? store;
    readonly bool includeDetached;

    internal OrderedIndex(OrderedStore<TKey>? store, bool includeDetached)
    {
        this.store = store;
        this.includeDetached = includeDetached;
    }

    /// <summary>Every entity whose key is at most <paramref name="upToInclusive"/>, in key order. Each <c>foreach</c> walks a snapshot, so
    /// the body may change values, add and destroy; an entity is skipped once it leaves the index or its key rises past the bound. The
    /// snapshot lives in world scratch, so a partitioned query body cannot take one.</summary>
    public DueEntities<TKey> Due(scoped in TKey upToInclusive) =>
        new(store, upToInclusive, includeDetached);

    /// <summary>The entry with the smallest key.</summary>
    public bool TryPeek([MaybeNullWhen(false)] out TKey key, out Entity entity)
    {
        if (store is not null)
            return store.TryPeek(includeDetached, out key, out entity);
        key = default;
        entity = default;
        return false;
    }

    /// <summary>How many entities the index lists.</summary>
    public int Count => store?.Count(includeDetached) ?? 0;

    /// <summary>The same index, also listing detached entities.</summary>
    public OrderedIndex<TKey> IncludeDetached() => new(store, true);
}

/// <summary>The entities <see cref="OrderedIndex{TKey}.Due"/> found; each <c>foreach</c> takes a fresh snapshot.</summary>
public readonly ref struct DueEntities<TKey>
    where TKey : notnull
{
    readonly OrderedStore<TKey>? store;
    readonly TKey upTo;
    readonly bool includeDetached;

    internal DueEntities(OrderedStore<TKey>? store, scoped in TKey upTo, bool includeDetached)
    {
        this.store = store;
        this.upTo = upTo;
        this.includeDetached = includeDetached;
    }

    /// <summary>Snapshots the due entities from world scratch and walks them in key order.</summary>
    /// <exception cref="InvalidOperationException">A partitioned query body is running.</exception>
    public DueEnumerator<TKey> GetEnumerator() => new(store, upTo, includeDetached);
}

/// <summary>Walks a snapshot of due entities taken from world scratch; disposing it returns the scratch.</summary>
[MustDispose("its snapshot never returns to the pool")]
public ref struct DueEnumerator<TKey>
    where TKey : notnull
{
    readonly OrderedStore<TKey>? store;
    readonly TKey upTo;
    readonly bool includeDetached;
    ScratchBuffer<Entity> snapshot;
    int at;

    internal DueEnumerator(OrderedStore<TKey>? store, scoped in TKey upTo, bool includeDetached)
    {
        this.store = store;
        this.upTo = upTo;
        this.includeDetached = includeDetached;
        snapshot = store is null ? default : store.Snapshot(upTo, includeDetached);
        at = -1;
    }

    /// <summary>The due entity at the current position.</summary>
    public readonly Entity Current => snapshot.Span[at];

    /// <summary>Advances to the next snapshot entry still in the index with its key within the bound; false when none is left.</summary>
    public bool MoveNext()
    {
        if (store is null)
            return false;
        var due = snapshot.Span;
        while (++at < due.Length)
        {
            if (store.IsDue(due[at], upTo, includeDetached))
                return true;
        }
        return false;
    }

    /// <summary>Returns the snapshot to world scratch.</summary>
    public void Dispose() => snapshot.Dispose();
}

internal sealed class OrderedStore<TKey> : IndexStore
    where TKey : notnull
{
    struct Entry
    {
        public TKey Key;
        public ulong Sequence;
        public bool Detached;
        public int Position;
    }

    sealed class Heap
    {
        public int[] Ids = new int[16];
        public int Count;

        public void Load(Heap source)
        {
            Copies.Load(ref Ids, source.Ids, source.Count);
            Count = source.Count;
        }
    }

    readonly struct ByKey(Entry[] items) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            var order = Order.Compare(items[a].Key, items[b].Key);
            return order != 0 ? order : items[a].Sequence.CompareTo(items[b].Sequence);
        }
    }

    static readonly EqualityComparer<TKey> Keys = EqualityComparer<TKey>.Default;
    static readonly Comparer<TKey> Order = Comparer<TKey>.Default;

    readonly KeyReader<TKey> reader;
    readonly IndexEntries<Entry> entries = new();
    readonly Heap live = new();
    readonly Heap detached = new();
    ulong sequence;

    public OrderedStore(World world, int type, IndexDeclaration declaration, KeyReader<TKey> reader)
        : base(world, type, declaration) => this.reader = reader;

    protected override IndexStore Blank() =>
        new OrderedStore<TKey>(world, Type, Declaration, reader);

    public override void Load(IndexStore other)
    {
        var source = (OrderedStore<TKey>)other;
        entries.Load(source.entries);
        live.Load(source.live);
        detached.Load(source.detached);
        sequence = source.sequence;
    }

    public int Count(bool includeDetached) => live.Count + (includeDetached ? detached.Count : 0);

    public bool TryPeek(
        bool includeDetached,
        [MaybeNullWhen(false)] out TKey key,
        out Entity entity
    )
    {
        var best = live.Count > 0 ? live.Ids[0] : -1;
        if (includeDetached && detached.Count > 0 && (best < 0 || Before(detached.Ids[0], best)))
            best = detached.Ids[0];
        if (best < 0)
        {
            key = default;
            entity = default;
            return false;
        }
        key = entries.Items[best].Key;
        entity = entries.Owners[best];
        return true;
    }

    public bool IsDue(Entity entity, in TKey upTo, bool includeDetached)
    {
        var id = entries.Find(entity);
        if (id < 0)
            return false;
        ref readonly var entry = ref entries.Items[id];
        return (includeDetached || !entry.Detached) && Order.Compare(entry.Key, upTo) <= 0;
    }

    public ScratchBuffer<Entity> Snapshot(scoped in TKey upTo, bool includeDetached)
    {
        var capacity = Count(includeDetached);
        var scratch = world.Scratch;
        using var ids = scratch.Rent<int>(capacity);
        using var stack = scratch.Rent<int>(capacity);
        var count = Gather(live, upTo, ids.Span, stack.Span, 0);
        if (includeDetached)
            count = Gather(detached, upTo, ids.Span, stack.Span, count);
        var found = ids.Span[..count];
        found.Sort(new ByKey(entries.Items));
        var due = scratch.Rent<Entity>(count);
        for (var i = 0; i < count; i++)
            due.Span[i] = entries.Owners[found[i]];
        return due;
    }

    // A subtree whose root is past the bound holds only later keys, so the walk skips it.
    int Gather(Heap heap, scoped in TKey upTo, Span<int> ids, Span<int> stack, int count)
    {
        if (heap.Count == 0)
            return count;
        var depth = 0;
        stack[depth++] = 0;
        while (depth > 0)
        {
            var at = stack[--depth];
            var id = heap.Ids[at];
            if (Order.Compare(entries.Items[id].Key, upTo) > 0)
                continue;
            ids[count++] = id;
            var child = (2 * at) + 1;
            if (child < heap.Count)
                stack[depth++] = child;
            if (child + 1 < heap.Count)
                stack[depth++] = child + 1;
        }
        return count;
    }

    public override void Refresh(Entity entity)
    {
        if (reader.TryRead(world, entity, out var key))
            Refresh(entity, key);
        else
            Unkey(entity);
    }

    public override void Refresh(Entity entity, ref byte value)
    {
        if (reader.TryReadFrom(world, entity, ref value, out var key))
            Refresh(entity, key);
        else
            Unkey(entity);
    }

    void Unkey(Entity entity)
    {
        var id = Own(entity);
        if (id >= 0)
            Drop(id);
    }

    void Refresh(Entity entity, TKey key)
    {
        var id = Own(entity);
        var lane = world.IndexLane(entity);
        if (lane < 0)
        {
            if (id >= 0)
                Drop(id);
            return;
        }
        var isDetached = lane == 1;
        if (id >= 0)
        {
            ref readonly var entry = ref entries.Items[id];
            if (entry.Detached == isDetached && Keys.Equals(entry.Key, key))
                return;
            Pull(id);
        }
        else
            id = entries.Add(entity);
        ref var pushed = ref entries.Items[id];
        pushed.Key = key;
        pushed.Detached = isDetached;
        pushed.Sequence = ++sequence;
        Push(id);
    }

    public override bool KeyChanged(ref byte before, ref byte after) =>
        reader.KeyChanged(ref before, ref after);

    public override void Remove(Entity entity)
    {
        var id = entries.Slot(entity.Index);
        if (id >= 0)
            Drop(id);
    }

    public override void Clear()
    {
        live.Count = 0;
        detached.Count = 0;
        entries.Clear();
        sequence = 0;
    }

    int Own(Entity entity)
    {
        var id = entries.Slot(entity.Index);
        if (id >= 0 && entries.Owners[id] != entity)
        {
            Drop(id);
            return -1;
        }
        return id;
    }

    void Drop(int id)
    {
        Pull(id);
        entries.Free(id);
    }

    Heap HeapOf(int id) => entries.Items[id].Detached ? detached : live;

    bool Before(int a, int b)
    {
        var items = entries.Items;
        var order = Order.Compare(items[a].Key, items[b].Key);
        return order < 0 || (order == 0 && items[a].Sequence < items[b].Sequence);
    }

    void Push(int id)
    {
        var heap = HeapOf(id);
        if (heap.Count == heap.Ids.Length)
            Array.Resize(ref heap.Ids, heap.Count * 2);
        Up(heap, heap.Count++, id);
    }

    void Pull(int id)
    {
        var heap = HeapOf(id);
        var at = entries.Items[id].Position;
        var last = --heap.Count;
        if (at == last)
            return;
        var moved = heap.Ids[last];
        if (at > 0 && Before(moved, heap.Ids[(at - 1) >> 1]))
            Up(heap, at, moved);
        else
            Down(heap, at, moved);
    }

    void Up(Heap heap, int at, int id)
    {
        while (at > 0)
        {
            var parent = (at - 1) >> 1;
            if (!Before(id, heap.Ids[parent]))
                break;
            Place(heap, at, heap.Ids[parent]);
            at = parent;
        }
        Place(heap, at, id);
    }

    void Down(Heap heap, int at, int id)
    {
        while (true)
        {
            var child = (2 * at) + 1;
            if (child >= heap.Count)
                break;
            if (child + 1 < heap.Count && Before(heap.Ids[child + 1], heap.Ids[child]))
                child++;
            if (!Before(heap.Ids[child], id))
                break;
            Place(heap, at, heap.Ids[child]);
            at = child;
        }
        Place(heap, at, id);
    }

    void Place(Heap heap, int at, int id)
    {
        heap.Ids[at] = id;
        entries.Items[id].Position = at;
    }
}
