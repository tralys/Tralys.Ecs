using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>One key of a <see cref="ConflictPolicy.Throw"/> unique index that a second entity took while another held it.</summary>
public readonly record struct IndexConflict(
    string Index,
    object Key,
    Entity Holder,
    Entity Incoming
)
{
    /// <summary><c>Index: Incoming takes key Key, which Holder already holds.</c></summary>
    public override string ToString() =>
        $"{Index}: {Incoming} takes key {Key}, which {Holder} already holds.";
}

/// <summary>Thrown when a unique index declared with <see cref="ConflictPolicy.Throw"/> sees a second entity take a key, once the world
/// operation that made the conflict has finished and only if the key is still shared then. The index stays consistent: the earlier holder
/// keeps the key and the newcomer takes it once the holder leaves it. <see cref="Index"/>, <see cref="Key"/>, <see cref="Holder"/> and
/// <see cref="Incoming"/> describe the first conflict; <see cref="Conflicts"/> lists every one the operation made.</summary>
public sealed class IndexConflictException : InvalidOperationException
{
    /// <summary>An exception for one conflict: <paramref name="incoming"/> took <paramref name="key"/> of <paramref name="index"/> from
    /// <paramref name="holder"/>.</summary>
    public IndexConflictException(string index, object key, Entity holder, Entity incoming)
        : this([new IndexConflict(index, key, holder, incoming)]) { }

    internal IndexConflictException(IReadOnlyList<IndexConflict> conflicts)
        : base(string.Join(Environment.NewLine, conflicts)) => Conflicts = conflicts;

    /// <summary>Every conflict the operation made; never empty.</summary>
    public IReadOnlyList<IndexConflict> Conflicts { get; }

    /// <summary>The first conflict's index, as <c>Component.Field</c>.</summary>
    public string Index => Conflicts[0].Index;

    /// <summary>The first conflict's key.</summary>
    public object Key => Conflicts[0].Key;

    /// <summary>The first conflict's earlier holder, which keeps the key.</summary>
    public Entity Holder => Conflicts[0].Holder;

    /// <summary>The first conflict's newcomer, which takes the key once the holder leaves it.</summary>
    public Entity Incoming => Conflicts[0].Incoming;
}

internal abstract class IndexStore
{
    protected readonly World world;
    public readonly int Type;
    public readonly IndexDeclaration Declaration;

    protected IndexStore(World world, int type, IndexDeclaration declaration)
    {
        this.world = world;
        Type = type;
        Declaration = declaration;
    }

    protected abstract IndexStore Blank();

    public abstract void Load(IndexStore source);

    public IndexStore Clone()
    {
        var copy = Blank();
        copy.Load(this);
        return copy;
    }

    public abstract void Refresh(Entity entity);

    public abstract void Refresh(Entity entity, ref byte value);

    public abstract bool KeyChanged(ref byte before, ref byte after);

    public abstract void Remove(Entity entity);

    public abstract void Clear();

    public virtual void TakeConflicts(List<IndexConflict> into) { }

    public virtual bool HasConflicts => false;
}

internal abstract class KeyReader<TKey>
    where TKey : notnull
{
    public abstract bool TryRead(World world, Entity entity, out TKey key);

    public abstract bool TryReadFrom(World world, Entity entity, ref byte value, out TKey key);

    public abstract bool KeyChanged(ref byte before, ref byte after);
}

internal sealed class KeyReader<TComponent, TKey, TKeyer>(bool perParent) : KeyReader<TKey>
    where TComponent : struct
    where TKey : notnull
    where TKeyer : IIndexKey<TComponent, TKey>
{
    public override bool TryRead(World world, Entity entity, out TKey key)
    {
        ref var value = ref world.Locate<TComponent>(
            entity,
            ComponentType<TComponent>.Index,
            out _
        );
        if (Unsafe.IsNullRef(ref value))
        {
            key = default!;
            return false;
        }
        return TryReadFrom(world, entity, ref Unsafe.As<TComponent, byte>(ref value), out key);
    }

    public override bool TryReadFrom(World world, Entity entity, ref byte value, out TKey key) =>
        TKeyer.TryOf(
            in Unsafe.As<byte, TComponent>(ref value),
            perParent ? world.hierarchy.ParentOf(entity) : default,
            out key
        );

    public override bool KeyChanged(ref byte before, ref byte after)
    {
        var had = TKeyer.TryOf(in Unsafe.As<byte, TComponent>(ref before), default, out var was);
        var has = TKeyer.TryOf(in Unsafe.As<byte, TComponent>(ref after), default, out var now);
        return had != has || (had && !EqualityComparer<TKey>.Default.Equals(was, now));
    }
}

internal sealed class IndexEntries<T>
    where T : struct
{
    readonly EntityMap map = new();
    public T[] Items = new T[16];
    public Entity[] Owners = new Entity[16];
    int count;
    int[] free = new int[16];
    int freeCount;

    // Items and Owners grow together, so they keep one length.
    public void Load(IndexEntries<T> source)
    {
        map.Load(source.map);
        if (Items.Length < source.count)
        {
            Items = new T[source.Items.Length];
            Owners = new Entity[source.Items.Length];
        }
        Copies.Load(ref Items, source.Items, source.count);
        Copies.Load(ref Owners, source.Owners, source.count);
        count = source.count;
        Copies.Load(ref free, source.free, source.freeCount);
        freeCount = source.freeCount;
    }

    public int Slot(uint index) => map.Get(index);

    public int Find(Entity entity)
    {
        var id = map.Get(entity.Index);
        return id >= 0 && Owners[id] == entity ? id : -1;
    }

    public int Add(Entity entity)
    {
        int id;
        if (freeCount > 0)
            id = free[--freeCount];
        else
        {
            id = count++;
            if (id == Items.Length)
            {
                Array.Resize(ref Items, id * 2);
                Array.Resize(ref Owners, id * 2);
            }
        }
        Items[id] = default;
        Owners[id] = entity;
        map.Set(entity.Index, id);
        return id;
    }

    public void Free(int id)
    {
        map.Remove(Owners[id].Index);
        Owners[id] = default;
        Items[id] = default;
        if (freeCount == free.Length)
            Array.Resize(ref free, freeCount * 2);
        free[freeCount++] = id;
    }

    public void Clear()
    {
        map.Clear();
        Array.Clear(Items, 0, count);
        Array.Clear(Owners, 0, count);
        count = 0;
        freeCount = 0;
    }
}
