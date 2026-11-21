using System.Runtime.InteropServices;

namespace Tralys.Ecs;

/// <summary>Key → the one entity holding it. Pending-destroy entities are never found; detached ones only through
/// <see cref="IncludeDetached"/>, which prefers a live holder.</summary>
public readonly ref struct UniqueIndex<TKey>
    where TKey : notnull
{
    readonly UniqueStore<TKey>? store;
    readonly bool includeDetached;

    internal UniqueIndex(UniqueStore<TKey>? store, bool includeDetached)
    {
        this.store = store;
        this.includeDetached = includeDetached;
    }

    /// <summary>The entity holding <paramref name="key"/>; false when none does.</summary>
    public bool TryGet(in TKey key, out Entity entity)
    {
        if (store is not null)
            return store.TryGet(key, includeDetached, out entity);
        entity = default;
        return false;
    }

    /// <summary>The entity holding <paramref name="key"/>.</summary>
    /// <exception cref="KeyNotFoundException">No entity holds <paramref name="key"/>.</exception>
    public Entity Get(in TKey key) =>
        TryGet(key, out var entity)
            ? entity
            : throw new KeyNotFoundException($"No entity holds the key {key}.");

    /// <summary>Some entity holds <paramref name="key"/>.</summary>
    public bool Contains(in TKey key) => TryGet(key, out _);

    /// <summary>The same index, also finding detached entities.</summary>
    public UniqueIndex<TKey> IncludeDetached() => new(store, true);
}

internal sealed class UniqueStore<TKey> : IndexStore
    where TKey : notnull
{
    struct Entry
    {
        public TKey Key;
        public bool Detached;
        public int Prev;
        public int Next;
    }

    struct Head
    {
        public int First;
        public int Last;
        public Entity Holder;
    }

    struct Conflict
    {
        public TKey Key;
        public bool Detached;
        public Entity Incoming;
    }

    static readonly EqualityComparer<TKey> Keys = EqualityComparer<TKey>.Default;

    readonly KeyReader<TKey> reader;
    readonly bool latestWins;
    readonly bool throws;
    readonly Dictionary<TKey, Head> live = [];
    Dictionary<TKey, Head>? detached;
    readonly IndexEntries<Entry> entries = new();
    Conflict[] conflicts = [];
    int conflictCount;

    public UniqueStore(World world, int type, IndexDeclaration declaration, KeyReader<TKey> reader)
        : base(world, type, declaration)
    {
        this.reader = reader;
        latestWins = declaration.OnConflict == ConflictPolicy.ReplaceExisting;
        throws = declaration.OnConflict == ConflictPolicy.Throw;
    }

    protected override IndexStore Blank() =>
        new UniqueStore<TKey>(world, Type, Declaration, reader);

    public override void Load(IndexStore other)
    {
        var source = (UniqueStore<TKey>)other;
        Copies.Load(live, source.live);
        Copies.Load(ref detached, source.detached);
        entries.Load(source.entries);
        Copies.Load(ref conflicts, source.conflicts, source.conflictCount);
        conflictCount = source.conflictCount;
    }

    public bool TryGet(in TKey key, bool includeDetached, out Entity entity)
    {
        if (
            live.TryGetValue(key, out var head)
            || (includeDetached && detached is not null && detached.TryGetValue(key, out head))
        )
        {
            entity = head.Holder;
            return true;
        }
        entity = default;
        return false;
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
            Unlink(id);
        }
        else
            id = entries.Add(entity);
        ref var linked = ref entries.Items[id];
        linked.Key = key;
        linked.Detached = isDetached;
        Link(id);
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
        live.Clear();
        detached?.Clear();
        entries.Clear();
        Array.Clear(conflicts, 0, conflictCount);
        conflictCount = 0;
    }

    public override bool HasConflicts => conflictCount > 0;

    public override void TakeConflicts(List<IndexConflict> into)
    {
        var name = $"{world.registry[Type].Name}.{Declaration.Name}";
        var start = into.Count;
        for (var i = 0; i < conflictCount; i++)
        {
            ref readonly var conflict = ref conflicts[i];
            var id = entries.Find(conflict.Incoming);
            if (id < 0)
                continue;
            ref readonly var entry = ref entries.Items[id];
            if (entry.Detached != conflict.Detached || !Keys.Equals(entry.Key, conflict.Key))
                continue;
            var holder = Lane(entry.Detached)[entry.Key].Holder;
            if (holder == conflict.Incoming)
                continue;
            var found = new IndexConflict(name, conflict.Key, holder, conflict.Incoming);
            if (!into.Skip(start).Contains(found))
                into.Add(found);
        }
        Array.Clear(conflicts, 0, conflictCount);
        conflictCount = 0;
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

    Dictionary<TKey, Head> Lane(bool isDetached) => isDetached ? detached ??= [] : live;

    void Link(int id)
    {
        var items = entries.Items;
        ref var entry = ref items[id];
        entry.Next = -1;
        var owner = entries.Owners[id];
        ref var head = ref CollectionsMarshal.GetValueRefOrAddDefault(
            Lane(entry.Detached),
            entry.Key,
            out var exists
        );
        if (!exists)
        {
            entry.Prev = -1;
            head = new Head
            {
                First = id,
                Last = id,
                Holder = owner,
            };
            return;
        }
        items[head.Last].Next = id;
        entry.Prev = head.Last;
        head.Last = id;
        if (latestWins)
            head.Holder = owner;
        else if (throws)
            Contest(entry.Key, entry.Detached, owner);
    }

    void Unlink(int id)
    {
        var items = entries.Items;
        ref readonly var entry = ref items[id];
        var lane = Lane(entry.Detached);
        if (entry.Prev < 0 && entry.Next < 0)
        {
            lane.Remove(entry.Key);
            return;
        }
        ref var head = ref CollectionsMarshal.GetValueRefOrNullRef(lane, entry.Key);
        if (entry.Prev >= 0)
            items[entry.Prev].Next = entry.Next;
        else
            head.First = entry.Next;
        if (entry.Next >= 0)
            items[entry.Next].Prev = entry.Prev;
        else
            head.Last = entry.Prev;
        if (head.Holder == entries.Owners[id])
            head.Holder = entries.Owners[latestWins ? head.Last : head.First];
    }

    void Drop(int id)
    {
        Unlink(id);
        entries.Free(id);
    }

    void Contest(in TKey key, bool isDetached, Entity incoming)
    {
        if (conflictCount == conflicts.Length)
            Array.Resize(ref conflicts, Math.Max(4, conflictCount * 2));
        conflicts[conflictCount++] = new Conflict
        {
            Key = key,
            Detached = isDetached,
            Incoming = incoming,
        };
        if (conflictCount == 1)
            world.Contested(this);
    }
}
