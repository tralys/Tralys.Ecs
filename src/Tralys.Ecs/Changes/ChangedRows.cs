using System.Diagnostics;

namespace Tralys.Ecs;

/// <summary>One row of <typeparamref name="TRow"/> stamped inside a sample's window.</summary>
public readonly ref struct ChangedRow<TKey, TRow>
    where TKey : notnull
    where TRow : struct, IRow<TKey>
{
    readonly ref readonly TRow value;

    internal ChangedRow(Entity entity, TKey key, ref readonly TRow value, bool added, ulong version)
    {
        Entity = entity;
        Key = key;
        this.value = ref value;
        Added = added;
        Version = version;
    }

    /// <summary>The entity owning the row.</summary>
    public Entity Entity { get; }

    /// <summary>The row's key.</summary>
    public TKey Key { get; }

    /// <summary>The row's current value, by reference into storage; valid until a row of <typeparamref name="TRow"/> is next added or
    /// removed on the owner.</summary>
    public ref readonly TRow Value => ref value;

    /// <summary>The row itself arrived inside the window, rather than only its value changing.</summary>
    public bool Added { get; }

    /// <summary>The version the row was last stamped at.</summary>
    public ulong Version { get; }
}

/// <summary>Rows of <typeparamref name="TRow"/> stamped inside a version window, owner by owner; an owner with no row stamped since the
/// window opened is never visited. Enumerate it before the world adds or removes a row of <typeparamref name="TRow"/>; a value written
/// meanwhile shows in the next sample. One walk of <typeparamref name="TRow"/> may be open at a time, from any sample: starting another
/// before it ends throws <see cref="InvalidOperationException"/>.</summary>
public readonly ref struct ChangedRows<TKey, TRow>
    where TKey : notnull
    where TRow : struct, IRow<TKey>
{
    readonly World world;
    readonly ulong since;
    readonly ulong until;
    readonly CursorOptions options;

    internal ChangedRows(World world, ulong since, ulong until, CursorOptions options)
    {
        Debug.Assert(world.registry[world.registry.IndexOf<TRow>()].IsTracked);
        this.world = world;
        this.since = since;
        this.until = until;
        this.options = options;
    }

    /// <summary>Starts a walk of the stamped rows, owner by owner.</summary>
    /// <exception cref="InvalidOperationException">Another walk of <typeparamref name="TRow"/> is open.</exception>
    public Enumerator GetEnumerator() => new(world, since, until, options);

    /// <summary>Walks a <see cref="ChangedRows{TKey, TRow}"/>; dispose it to end the walk.</summary>
    [MustDispose("every later walk of the rows type throws")]
    public ref struct Enumerator
    {
        readonly World world;
        readonly RowStore<TKey, TRow>? store;
        readonly ulong since;
        readonly ulong until;
        readonly CursorOptions options;
        RowStore<TKey, TRow>.Block? block;
        RowStore<TKey, TRow>? walking;
        int older;
        int row;
        ulong stamp;

        internal Enumerator(World world, ulong since, ulong until, CursorOptions options)
        {
            this.world = world;
            this.since = since;
            this.until = until;
            this.options = options;
            store = world.FindRows<TKey, TRow>();
            older = store?.BeginWalk() ?? -1;
            walking = store;
            row = -1;
        }

        /// <summary>Ends the walk, so another walk of <typeparamref name="TRow"/> may start; a second dispose does nothing.</summary>
        public void Dispose()
        {
            walking?.EndWalk();
            walking = null;
        }

        /// <summary>The row at the current position.</summary>
        public readonly ChangedRow<TKey, TRow> Current
        {
            get
            {
                if (block is null)
                    return default;
                ref var value = ref block.Rows[row];
                return new(block.Owner, value.RowKey, ref value, block.Added[row] > since, stamp);
            }
        }

        /// <summary>Moves to the next row stamped inside the window; false past the last.</summary>
        public bool MoveNext()
        {
            while (true)
            {
                if (block is not null)
                {
                    while (++row < block.Count)
                    {
                        stamp = block.Changed[row];
                        if (stamp > since && stamp <= until && !block.Dead[row])
                            return true;
                    }
                }
                if (!NextBlock())
                    return false;
            }
        }

        bool NextBlock()
        {
            block = null;
            while (older >= 0 && store is not null)
            {
                var next = store.At(older);
                if (next.MaxChanged <= since)
                    break;
                older = next.Older;
                if (!world.Admits(next.Owner, options))
                    continue;
                block = next;
                row = -1;
                return true;
            }
            older = -1;
            return false;
        }
    }
}
