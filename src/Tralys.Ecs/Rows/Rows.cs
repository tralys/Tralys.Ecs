using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>An entity's keyed rows of <typeparamref name="TRow"/>, in insertion order. Upserts and removals are legal while enumerating:
/// a removed row is skipped, an inserted one is visited only by later enumerations. Bound to no world, as from <c>default(WorldEntity)</c>,
/// it reads as no rows, <see cref="Remove"/> and <see cref="Clear"/> do nothing, and every other write throws.</summary>
public readonly ref struct Rows<TKey, TRow>
    where TKey : notnull
    where TRow : struct, IRow<TKey>
{
    readonly World? world;
    readonly Entity entity;

    internal Rows(World world, Entity entity)
    {
        this.world = world;
        this.entity = entity;
    }

    World Bound => world ?? throw World.NotAlive(entity);

    RowStore<TKey, TRow>? Store => world?.FindRows<TKey, TRow>();

    RowStore<TKey, TRow>.Block? Block => Store?.BlockOf(entity);

    /// <summary>How many rows the entity has.</summary>
    public int Count => Block?.Live ?? 0;

    /// <summary>The entity has a row keyed <paramref name="key"/>.</summary>
    [Effect(Effects.None)]
    public bool Contains(in TKey key) =>
        Store is { } store && store.BlockOf(entity) is { } block && store.Find(block, key) >= 0;

    /// <summary>Copies the row keyed <paramref name="key"/> out; false when there is none.</summary>
    [Effect(Effects.None)]
    public bool TryGet(in TKey key, out TRow row)
    {
        ref readonly var found = ref RowRef(key);
        if (Unsafe.IsNullRef(in found))
        {
            row = default;
            return false;
        }
        row = found;
        return true;
    }

    /// <summary>The row by <c>ref readonly</c>, or an empty optional when there is none for <paramref name="key"/>.</summary>
    [Effect(Effects.None)]
    public Optional<TRow> Find(in TKey key) => new(in RowRef(key));

    ref readonly TRow RowRef(scoped in TKey key)
    {
        var store = Store;
        if (store?.BlockOf(entity) is { } block && store.Find(block, key) is var at and >= 0)
            return ref block.Rows[at];
        return ref Unsafe.NullRef<TRow>();
    }

    /// <summary>The row for <paramref name="key"/>, which must exist. The edit stamps the row on dispose only if its value changed. While the
    /// edit is open, the entity's rows of this type cannot be added or removed.</summary>
    /// <exception cref="InvalidOperationException">The entity has no row for <paramref name="key"/>.</exception>
    [Effect(Effects.Mutates)]
    public RowEdit<TKey, TRow> Edit(
        in TKey key,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        var world = Bound;
        world.At(file, line);
        world.CheckWritable();
        world.Slot(entity);
        var store = world.FindRows<TKey, TRow>();
        if (
            store?.BlockOf(entity) is not { } block
            || store.Find(block, key) is not (var at and >= 0)
        )
            throw new InvalidOperationException(
                $"{entity} has no {typeof(TRow).Name} row for {key}."
            );
        return new RowEdit<TKey, TRow>(store, block, at, false);
    }

    /// <summary>The row for <paramref name="key"/>, added with its other fields default if missing. The edit stamps the row on dispose only
    /// if its value changed, and an added row reaches add observers on dispose with its final value. While the edit is open, the entity's
    /// rows of this type cannot be added or removed.</summary>
    [Effect(Effects.Mutates)]
    public RowEdit<TKey, TRow> EditOrAdd(
        in TKey key,
        out bool added,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        var world = Bound;
        world.At(file, line);
        world.CheckWritable();
        world.Slot(entity);
        world.CheckRowsExist(entity);
        var store = world.RowsOf<TKey, TRow>();
        var at = store.Upsert(entity, key, out var block, out added);
        return new RowEdit<TKey, TRow>(store, block, at, added);
    }

    /// <summary>Adds or overwrites the row with <paramref name="row"/>'s key; an overwrite stamps only if the value differs. On an entity
    /// created by an observer it waits for the next playback, when the entity exists.</summary>
    [Effect(Effects.Mutates)]
    public void Set(in TRow row, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var world = Bound;
        world.At(file, line);
        world.CheckWritable();
        world.Slot(entity);
        if (world.RecordsRow(entity, CommandKind.UpsertRow, row))
            return;
        world
            .RowsOf<TKey, TRow>()
            .ApplyUpsert(
                world,
                entity,
                ref Unsafe.As<TRow, byte>(ref Unsafe.AsRef(in row)),
                default
            );
    }

    /// <summary>Removes the row keyed <paramref name="key"/>, at once even inside a query body; false when there is none. On an entity
    /// created by an observer it waits for the next playback and returns false.</summary>
    /// <exception cref="InvalidOperationException">An edit of one of the entity's rows of this type is open.</exception>
    [Effect(Effects.Mutates)]
    public bool Remove(
        in TKey key,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (world is null)
            return false;
        world.At(file, line);
        world.CheckWritable();
        var keyed = default(TRow);
        keyed.RowKey = key;
        if (world.RecordsRow(entity, CommandKind.RemoveRow, keyed))
            return false;
        var store = world.FindRows<TKey, TRow>();
        if (
            store?.BlockOf(entity) is not { } block
            || store.Find(block, key) is not (var at and >= 0)
        )
            return false;
        store.Remove(block, at, default);
        return true;
    }

    /// <summary>Removes every row of <typeparamref name="TRow"/> on the entity.</summary>
    /// <exception cref="InvalidOperationException">The entity was created by an observer and exists only at playback.</exception>
    [Effect(Effects.Mutates)]
    public void Clear([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (world is null)
            return;
        world.At(file, line);
        world.CheckWritable();
        world.CheckRowsExist(entity);
        var store = world.FindRows<TKey, TRow>();
        if (store?.BlockOf(entity) is { } block)
            store.RemoveAll(block, default);
    }

    /// <summary>Walks the rows present when it starts, in insertion order, skipping any removed meanwhile.</summary>
    [Effect(Effects.None)]
    public Enumerator GetEnumerator() => new(world, Store, Block);

    /// <summary>Walks one entity's rows; dispose it to end the iteration.</summary>
    [MustDispose("the entity's rows stay marked as iterating")]
    public ref struct Enumerator
    {
        readonly World? world;
        readonly RowStore<TKey, TRow>? store;
        readonly RowStore<TKey, TRow>.Block? block;
        readonly int end;
        int index;

        internal Enumerator(
            World? world,
            RowStore<TKey, TRow>? store,
            RowStore<TKey, TRow>.Block? block
        )
        {
            this.world = world;
            this.store = store;
            this.block = block;
            index = -1;
            if (block is null)
                return;
            block.Iterating++;
            end = block.Count;
        }

        /// <summary>The current row by <c>ref readonly</c>.</summary>
        public readonly ref readonly TRow Current =>
            ref block is null ? ref Unsafe.NullRef<TRow>() : ref block.Rows[index];

        /// <summary>Advances to the next row not removed; false when none is left.</summary>
        [Effect(Effects.None)]
        public bool MoveNext()
        {
            if (block is null)
                return false;
            while (++index < end)
            {
                if (!block.Dead[index])
                    return true;
            }
            return false;
        }

        /// <summary>Removes the current row; enumeration continues with the next one.</summary>
        [Effect(Effects.Mutates)]
        public readonly void RemoveCurrent(
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0
        )
        {
            if (world is null || store is null || block is null)
                return;
            world.At(file, line);
            world.CheckWritable();
            if (!block.Dead[index])
                store.Remove(block, index, default);
        }

        /// <summary>Ends the iteration; rows removed during it are compacted once no other iteration or edit is open.</summary>
        [Effect(Effects.None)]
        public void Dispose()
        {
            if (block is not null)
                store?.EndIteration(block);
        }
    }
}

/// <summary>A scoped <c>ref</c> to one row. Disposing stamps the row's changed version only if its value changed.</summary>
[MustDispose("its change stamp is lost and the rows stay locked against adds and removes")]
public ref struct RowEdit<TKey, TRow>
    where TKey : notnull
    where TRow : struct, IRow<TKey>
{
    RowStore<TKey, TRow>? store;
    readonly RowStore<TKey, TRow>.Block block;
    readonly Entity owner;
    readonly int at;
    readonly int moves;
    readonly bool added;
    readonly TRow original;

    internal RowEdit(
        RowStore<TKey, TRow> store,
        RowStore<TKey, TRow>.Block block,
        int at,
        bool added
    )
    {
        this.store = store;
        this.block = block;
        this.at = at;
        this.added = added;
        owner = block.Owner;
        moves = block.Moves;
        original = block.Rows[at];
        store.BeginEdit(block);
    }

    /// <summary>The row by <c>ref</c>; writes land at once, and the key must not change.</summary>
    public readonly ref TRow Value => ref block.Rows[at];

    /// <summary>Stamps the row if its value changed and, for a row the edit added, notifies add observers; a second dispose does
    /// nothing.</summary>
    /// <exception cref="InvalidOperationException">The entity's rows were released or moved while the edit was open, or the edit changed
    /// the row's key.</exception>
    public void Dispose()
    {
        if (store is not { } rows)
            return;
        store = null;
        if (block.Owner != owner)
            throw new InvalidOperationException(
                "The entity's rows were released while this edit was open."
            );
        try
        {
            if (block.Moves != moves)
                throw new InvalidOperationException(
                    "The entity's rows moved while this edit was open, so its ref may point at another row."
                );
            var copy = original;
            if (!EqualityComparer<TKey>.Default.Equals(copy.RowKey, block.Rows[at].RowKey))
                throw new InvalidOperationException("An edit cannot change its row's key.");
            if (
                !ComponentOpsOf<TRow>.Equals(
                    ref Unsafe.As<TRow, byte>(ref copy),
                    ref Unsafe.As<TRow, byte>(ref block.Rows[at])
                )
            )
                rows.Changed(block, at, in copy);
            if (added)
                rows.NotifyAdded(block, at, default);
        }
        finally
        {
            rows.EndEdit(block);
        }
    }
}

public sealed partial class World
{
    internal readonly RowStore?[] rowStores;

    internal RowStore<TKey, TRow>? FindRows<TKey, TRow>()
        where TKey : notnull
        where TRow : struct, IRow<TKey> =>
        Unsafe.As<RowStore<TKey, TRow>?>(rowStores[layout.RowSlot[registry.IndexOf<TRow>()]]);

    internal RowStore RowsOf(int type)
    {
        var slot = layout.RowSlot[type];
        if (slot < 0)
            throw new InvalidOperationException($"{registry[type].Name} is not a row type.");
        if (rowStores[slot] is { } existing)
            return existing;
        var factory = registry[type].RowFactory;
        if (!factory.IsSet)
            throw new InvalidOperationException(
                $"{registry[type].Name} declares no [RowKey] field, so it has no row store."
            );
        return rowStores[slot] = factory.Invoke(this, type);
    }

    internal RowStore<TKey, TRow> RowsOf<TKey, TRow>()
        where TKey : notnull
        where TRow : struct, IRow<TKey> =>
        Unsafe.As<RowStore<TKey, TRow>>(RowsOf(registry.IndexOf<TRow>()));

    /// <summary>The keyed rows of <typeparamref name="TRow"/> on <paramref name="entity"/>, for code generic over the row type, such as a
    /// closed generic <see cref="RowsAttribute"/> type, which gets no per-type accessor. Reading never creates storage.</summary>
    [Effect(Effects.None)]
    public Rows<TKey, TRow> Rows<TKey, TRow>(Entity entity)
        where TKey : notnull
        where TRow : struct, IRow<TKey>
    {
        return new(this, entity);
    }

    /// <summary>The entities holding a row keyed <paramref name="key"/>; the row type needs <see cref="RowsAttribute.ReverseKeyIndex"/>. The list
    /// throws once the key gains or loses an owner, though one read while it had none may stay empty.</summary>
    [Effect(Effects.None)]
    public EntityList RowOwners<TKey, TRow>(in TKey key)
        where TKey : notnull
        where TRow : struct, IRow<TKey>
    {
        var store = FindRows<TKey, TRow>();
        if (store is not null)
            return store.ListOwners(key);
        if ((registry.Get<TRow>().Traits & ComponentTraits.ReverseKeyIndex) == 0)
            throw new InvalidOperationException(
                $"{typeof(TRow).Name} has no reverse key index; declare it [Rows(ReverseKeyIndex = true)]."
            );
        return default;
    }
}
