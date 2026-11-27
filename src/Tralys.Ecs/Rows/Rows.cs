using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>An entity's keyed rows of <typeparamref name="TRow"/>, in insertion order. Upserts and removals are legal while enumerating:
/// a removed row is skipped, an inserted one is visited only by later enumerations. Inside a query walk, and on an entity an observer
/// created, a write that adds or removes a row waits, as does every later write to these rows; it applies when the walk closes or at
/// the next playback, and reads see it then. Bound to no world, as from <c>default(WorldEntity)</c>, it reads as no rows,
/// <see cref="Remove"/> and <see cref="Clear"/> do nothing, and every other write throws.</summary>
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
    public Optional<TRow> Find(in TKey key)
    {
        if (
            world?.FindRows<TKey, TRow>() is not { } store
            || store.BlockOf(entity) is not { } block
            || store.Find(block, key) is not (var at and >= 0)
        )
            return default;
        return new(in block.Rows[at], world.checkBorrows ? BorrowCheck.Of(block) : default);
    }

    ref readonly TRow RowRef(scoped in TKey key)
    {
        var store = Store;
        if (store?.BlockOf(entity) is { } block && store.Find(block, key) is var at and >= 0)
            return ref block.Rows[at];
        return ref Unsafe.NullRef<TRow>();
    }

    /// <summary>The row for <paramref name="key"/>, which must exist. The edit stamps the row on dispose only if its value changed. While the
    /// edit is open, the entity's rows of this type cannot be added or removed. While these rows have a write waiting, the edit starts
    /// from the last waiting value and waits with it.</summary>
    /// <exception cref="InvalidOperationException">The entity has no row for <paramref name="key"/>, counting waiting writes.</exception>
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
        var type = world.registry.IndexOf<TRow>();
        if (world.RecordsRow(entity, type, reshapes: false))
        {
            if (!world.WaitingRow(entity, type, key, out TRow current))
                throw NoRow(key);
            return world.RecordedEdit<TKey, TRow>(entity, type, current);
        }
        var store = world.FindRows<TKey, TRow>();
        if (
            store?.BlockOf(entity) is not { } block
            || store.Find(block, key) is not (var at and >= 0)
        )
            throw NoRow(key);
        return new RowEdit<TKey, TRow>(store, block, at, false);
    }

    InvalidOperationException NoRow(in TKey key) =>
        new($"{entity} has no {typeof(TRow).Name} row for {key}.");

    /// <summary>The row for <paramref name="key"/>, added with its other fields default if missing. The edit stamps the row on dispose only
    /// if its value changed, and an added row reaches add observers on dispose with its final value. While the edit is open, the entity's
    /// rows of this type cannot be added or removed. When the add waits, as inside a query walk, the edit writes the waiting row, which
    /// applies with its final value.</summary>
    [Effect(Effects.Mutates | Effects.MovesRowBlocks)]
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
        var type = world.registry.IndexOf<TRow>();
        if (world.RecordsRow<TKey, TRow>(entity, type, key, adds: true))
        {
            added = !world.WaitingRow(entity, type, key, out TRow current);
            if (added)
                current.RowKey = key;
            return world.RecordedEdit<TKey, TRow>(entity, type, current);
        }
        var store = world.RowsOf<TKey, TRow>();
        var at = store.Upsert(entity, key, out var block, out added);
        return new RowEdit<TKey, TRow>(store, block, at, added);
    }

    /// <summary>Adds or overwrites the row with <paramref name="row"/>'s key; an overwrite stamps only if the value differs. An add waits
    /// inside a query walk, until it closes, and on an entity created by an observer, until the next playback.</summary>
    [Effect(Effects.Mutates | Effects.MovesRowBlocks)]
    public void Set(in TRow row, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var world = Bound;
        world.At(file, line);
        world.CheckWritable();
        world.Slot(entity);
        var type = world.registry.IndexOf<TRow>();
        if (world.RecordsRow<TKey, TRow>(entity, type, row.RowKey, adds: true))
        {
            world.RecordRow(entity, type, CommandKind.UpsertRow, row);
            return;
        }
        world
            .RowsOf<TKey, TRow>()
            .ApplyUpsert(
                world,
                entity,
                ref Unsafe.As<TRow, byte>(ref Unsafe.AsRef(in row)),
                default
            );
    }

    /// <summary>Removes the row keyed <paramref name="key"/>; false when there is none. Inside a query walk the removal waits until the walk
    /// closes, and on an entity created by an observer until the next playback; it then returns whether reads see the row now.</summary>
    /// <exception cref="InvalidOperationException">An edit of one of the entity's rows of this type is open.</exception>
    [Effect(Effects.Mutates | Effects.MovesRowBlocks)]
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
        var type = world.registry.IndexOf<TRow>();
        if (world.RecordsRow<TKey, TRow>(entity, type, key, adds: false))
        {
            var keyed = default(TRow);
            keyed.RowKey = key;
            world.RecordRow(entity, type, CommandKind.RemoveRow, keyed);
            return Contains(key);
        }
        var store = world.FindRows<TKey, TRow>();
        if (
            store?.BlockOf(entity) is not { } block
            || store.Find(block, key) is not (var at and >= 0)
        )
            return false;
        store.Remove(block, at, default);
        return true;
    }

    /// <summary>Removes every row of <typeparamref name="TRow"/> on the entity; where a removal would wait, as <see cref="Remove"/>'s
    /// does, each row's removal waits, waiting adds included.</summary>
    [Effect(Effects.Mutates | Effects.MovesRowBlocks)]
    public void Clear([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (world is null)
            return;
        world.At(file, line);
        world.CheckWritable();
        var type = world.registry.IndexOf<TRow>();
        if (world.RecordsRow(entity, type, reshapes: Count > 0))
        {
            world.RecordClearRows<TKey, TRow>(entity, type);
            return;
        }
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
        readonly Entity owner;
        readonly int end;
        readonly bool counted;
        readonly int generation;
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
            owner = block.Owner;
            end = block.Count;
            // A partitioned body cannot write rows, so only a serial walk needs compaction held off; workers must not share the counter.
            counted = world is not null && world.parallelDepth == 0;
            if (counted)
                generation = RowStore<TKey, TRow>.BeginIteration(block);
        }

        /// <summary>The current row by <c>ref readonly</c>.</summary>
        /// <exception cref="InvalidOperationException">The entity's rows were released or reloaded while the enumeration was open.</exception>
        public readonly ref readonly TRow Current
        {
            get
            {
                if (block is null)
                    return ref Unsafe.NullRef<TRow>();
                CheckGeneration(block);
                return ref block.Rows[index];
            }
        }

        readonly void CheckGeneration(RowStore<TKey, TRow>.Block held)
        {
            if (counted && held.Generation != generation)
                throw new InvalidOperationException(
                    "The entity's rows were released or reloaded while this enumeration was open."
                );
        }

        /// <summary>Advances to the next row not removed; false when none is left.</summary>
        /// <exception cref="InvalidOperationException">The entity's rows were released or reloaded while the enumeration was open.</exception>
        [Effect(Effects.None)]
        public bool MoveNext()
        {
            if (block is null)
                return false;
            CheckGeneration(block);
            while (++index < end)
            {
                if (!block.Dead[index])
                    return true;
            }
            return false;
        }

        /// <summary>Removes the current row; enumeration continues with the next one. Inside a query walk the removal waits until the walk
        /// closes.</summary>
        /// <exception cref="InvalidOperationException">The entity's rows were released or reloaded while the enumeration was open.</exception>
        [Effect(Effects.Mutates | Effects.MovesRowBlocks)]
        public readonly void RemoveCurrent(
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0
        )
        {
            if (world is null || store is null || block is null)
                return;
            world.At(file, line);
            world.CheckWritable();
            CheckGeneration(block);
            if (block.Dead[index])
                return;
            if (world.RecordsRow(owner, store.Type, reshapes: true))
            {
                var keyed = default(TRow);
                keyed.RowKey = block.Rows[index].RowKey;
                world.RecordRow(owner, store.Type, CommandKind.RemoveRow, keyed);
                return;
            }
            store.Remove(block, index, default);
        }

        /// <summary>Ends the iteration; rows removed during it are compacted once no other iteration or edit is open.</summary>
        [Effect(Effects.MovesRowBlocks)]
        public void Dispose()
        {
            if (counted && block is not null)
                store?.EndIteration(block, generation);
        }
    }
}

/// <summary>A scoped <c>ref</c> to one row. Disposing stamps the row's changed version only if its value changed.</summary>
[MustDispose("its change stamp is lost and the rows stay locked against adds and removes")]
[Borrow]
public ref struct RowEdit<TKey, TRow>
    where TKey : notnull
    where TRow : struct, IRow<TKey>
{
    RowStore<TKey, TRow>? store;
    readonly RowStore<TKey, TRow>.Block? block;
    readonly PayloadStore<TRow>? waiting;
    readonly Entity owner;
    readonly int at;
    readonly int moves;
    readonly int generation;
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
        generation = store.BeginEdit(block);
    }

    internal RowEdit(RowStore<TKey, TRow> store, PayloadStore<TRow> waiting, int at)
    {
        this.store = store;
        this.waiting = waiting;
        this.at = at;
        original = waiting[at];
        generation = waiting.Resets;
    }

    /// <summary>The row by <c>ref</c>; writes land at once, or in the waiting write the edit opened, and the key must not change.</summary>
    /// <exception cref="InvalidOperationException">The entity's rows were released or moved since the edit was opened, or the waiting
    /// write it opened has applied.</exception>
    public readonly ref TRow Value
    {
        get
        {
            if (waiting is not null)
            {
                CheckWaiting(waiting);
                return ref waiting[at];
            }
            if (block is not { } held)
                return ref Unsafe.NullRef<TRow>();
            if (store is not null)
            {
                CheckOwner(held);
                CheckMoves(held);
            }
            return ref held.Rows[at];
        }
    }

    readonly void CheckOwner(RowStore<TKey, TRow>.Block held)
    {
        if (held.Owner != owner)
            throw new InvalidOperationException(
                "The entity's rows were released while this edit was open."
            );
    }

    readonly void CheckWaiting(PayloadStore<TRow> held)
    {
        if (held.Resets != generation)
            throw new InvalidOperationException(
                "The waiting write this edit opened applied while the edit was open."
            );
    }

    readonly void CheckMoves(RowStore<TKey, TRow>.Block held)
    {
        if (held.Moves != moves)
            throw new InvalidOperationException(
                "The entity's rows moved while this edit was open, so its ref may point at another row."
            );
    }

    /// <summary>Stamps the row if its value changed and, for a row the edit added, notifies add observers; for a waiting write both
    /// happen when it applies. A second dispose does nothing.</summary>
    /// <exception cref="InvalidOperationException">The entity's rows were released or moved while the edit was open, its waiting write
    /// applied while it was open, or the edit changed the row's key.</exception>
    [Effect(Effects.Mutates | Effects.MovesRowBlocks)]
    public void Dispose()
    {
        if (store is not { } rows)
            return;
        store = null;
        if (waiting is not null)
        {
            CheckWaiting(waiting);
            if (!EqualityComparer<TKey>.Default.Equals(original.RowKey, waiting[at].RowKey))
                throw new InvalidOperationException("An edit cannot change its row's key.");
            return;
        }
        if (block is not { } held)
            return;
        CheckOwner(held);
        try
        {
            CheckMoves(held);
            var copy = original;
            if (!EqualityComparer<TKey>.Default.Equals(copy.RowKey, held.Rows[at].RowKey))
                throw new InvalidOperationException("An edit cannot change its row's key.");
            if (
                !ComponentOpsOf<TRow>.Equals(
                    ref Unsafe.As<TRow, byte>(ref copy),
                    ref Unsafe.As<TRow, byte>(ref held.Rows[at])
                )
            )
                rows.Changed(held, at, in copy);
            if (added)
                rows.NotifyAdded(held, at, default);
        }
        finally
        {
            rows.EndEdit(held, generation);
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

    // The last waiting write to the key decides, so an edit made while writes wait starts where they will leave the row.
    internal bool WaitingRow<TKey, TRow>(Entity owner, int type, in TKey key, out TRow row)
        where TKey : notnull
        where TRow : struct, IRow<TKey>
    {
        for (
            var i = stage.Latest(
                owner,
                type,
                CommandKind.UpsertRow,
                CommandKind.RemoveRow,
                stage.count
            );
            i >= 0;
            i = stage.Latest(owner, type, CommandKind.UpsertRow, CommandKind.RemoveRow, i)
        )
        {
            ref var recorded = ref stage.PayloadOf<TRow>(i);
            if (!EqualityComparer<TKey>.Default.Equals(recorded.RowKey, key))
                continue;
            var upserted = stage.KindOf(i) == CommandKind.UpsertRow;
            row = upserted ? recorded : default;
            return upserted;
        }
        if (
            FindRows<TKey, TRow>() is { } store
            && store.BlockOf(owner) is { } block
            && store.Find(block, key) is var at and >= 0
        )
        {
            row = block.Rows[at];
            return true;
        }
        row = default;
        return false;
    }

    internal RowEdit<TKey, TRow> RecordedEdit<TKey, TRow>(
        Entity owner,
        int type,
        scoped in TRow row
    )
        where TKey : notnull
        where TRow : struct, IRow<TKey>
    {
        var at = RecordRow(owner, type, CommandKind.UpsertRow, row);
        return new RowEdit<TKey, TRow>(RowsOf<TKey, TRow>(), stage.Payloads<TRow>(type), at);
    }

    internal void RecordClearRows<TKey, TRow>(Entity owner, int type)
        where TKey : notnull
        where TRow : struct, IRow<TKey>
    {
        var keyed = default(TRow);
        if (FindRows<TKey, TRow>()?.BlockOf(owner) is { } block)
        {
            for (var i = 0; i < block.Count; i++)
            {
                if (block.Dead[i])
                    continue;
                keyed.RowKey = block.Rows[i].RowKey;
                RecordRow(owner, type, CommandKind.RemoveRow, keyed);
            }
        }
        for (
            int recorded = stage.count,
                i = stage.Next(owner, type, CommandKind.UpsertRow, 0, recorded);
            i >= 0;
            i = stage.Next(owner, type, CommandKind.UpsertRow, i + 1, recorded)
        )
        {
            keyed.RowKey = stage.PayloadOf<TRow>(i).RowKey;
            RecordRow(owner, type, CommandKind.RemoveRow, keyed);
        }
    }

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
