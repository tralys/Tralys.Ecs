using System.Runtime.InteropServices;

namespace Tralys.Ecs;

/// <summary>Key → every entity holding it. Pending-destroy entities are never listed; detached ones only through
/// <see cref="IncludeDetached"/>.</summary>
public readonly ref struct MultiIndex<TKey>
    where TKey : notnull
{
    readonly MultiStore<TKey>? store;
    readonly bool includeDetached;

    internal MultiIndex(MultiStore<TKey>? store, bool includeDetached)
    {
        this.store = store;
        this.includeDetached = includeDetached;
    }

    /// <summary>The entities holding <paramref name="key"/>. The list throws once the key gains or loses a holder, which enabling or
    /// disabling the indexed component also does, though one read while the key had none may stay empty.</summary>
    public EntityList Get(scoped in TKey key) =>
        store is null ? default : store.Get(key, includeDetached);

    /// <summary>How many entities hold <paramref name="key"/>.</summary>
    public int Count(scoped in TKey key) => Get(key).Count;

    /// <summary>The same index, also listing detached entities.</summary>
    public MultiIndex<TKey> IncludeDetached() => new(store, true);
}

internal sealed class MultiStore<TKey> : IndexStore
    where TKey : notnull
{
    struct Entry
    {
        public TKey Key;
        public bool Detached;
        public int Position;
    }

    static readonly EqualityComparer<TKey> Keys = EqualityComparer<TKey>.Default;

    // Both lanes live as long as the key does, so a list over both sees a holder joining either.
    struct Lanes
    {
        public int Live;
        public int Detached;
    }

    readonly KeyReader<TKey> reader;
    readonly Dictionary<TKey, Lanes> lanes = [];
    readonly ListArena<Entity> lists = new();
    readonly IndexEntries<Entry> entries = new();

    public MultiStore(World world, int type, IndexDeclaration declaration, KeyReader<TKey> reader)
        : base(world, type, declaration) => this.reader = reader;

    protected override IndexStore Blank() => new MultiStore<TKey>(world, Type, Declaration, reader);

    public override void Load(IndexStore other)
    {
        var source = (MultiStore<TKey>)other;
        Copies.Load(lanes, source.lanes);
        lists.Load(source.lists);
        entries.Load(source.entries);
    }

    public EntityList Get(scoped in TKey key, bool includeDetached) =>
        lanes.TryGetValue(key, out var held)
            ? new EntityList(lists, held.Live, includeDetached ? held.Detached : -1)
            : default;

    public override void Refresh(Entity entity) =>
        Refresh(entity, reader.TryRead(world, entity, out var key), key);

    public override void Refresh(Entity entity, ref byte value) =>
        Refresh(entity, reader.TryReadFrom(world, entity, ref value, out var key), key);

    void Refresh(Entity entity, bool keyed, TKey key)
    {
        var id = Own(entity);
        var lane = world.IndexLane(entity);
        if (lane < 0 || !keyed)
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
            Unlist(id);
        }
        else
            id = entries.Add(entity);
        ref var listed = ref entries.Items[id];
        listed.Key = key;
        listed.Detached = isDetached;
        List(id);
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
        lanes.Clear();
        lists.Clear();
        entries.Clear();
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

    void List(int id)
    {
        ref var entry = ref entries.Items[id];
        ref var held = ref CollectionsMarshal.GetValueRefOrAddDefault(
            lanes,
            entry.Key,
            out var exists
        );
        if (!exists)
            held = new Lanes { Live = lists.New(), Detached = lists.New() };
        var list = entry.Detached ? held.Detached : held.Live;
        entry.Position = lists.Count(list);
        lists.Add(list, entries.Owners[id]);
    }

    void Unlist(int id)
    {
        ref readonly var entry = ref entries.Items[id];
        var held = lanes[entry.Key];
        var list = entry.Detached ? held.Detached : held.Live;
        var last = lists.Count(list) - 1;
        if (entry.Position != last)
        {
            var moved = lists.At(list, last);
            lists.At(list, entry.Position) = moved;
            entries.Items[entries.Slot(moved.Index)].Position = entry.Position;
        }
        lists.RemoveAt(list, last);
        if (lists.Count(held.Live) == 0 && lists.Count(held.Detached) == 0)
        {
            lists.Free(held.Live);
            lists.Free(held.Detached);
            lanes.Remove(entry.Key);
        }
    }

    void Drop(int id)
    {
        Unlist(id);
        entries.Free(id);
    }
}
