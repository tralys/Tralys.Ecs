namespace Tralys.Ecs;

/// <summary>One cursor sample: the structural log and changed values in the version window (<see cref="Since"/>, <see cref="Until"/>].</summary>
public readonly ref struct ChangeSample
{
    readonly World world;
    readonly CursorOptions options;

    internal ChangeSample(
        World world,
        ulong since,
        ulong until,
        CursorStatus status,
        CursorOptions options
    )
    {
        this.world = world;
        Since = since;
        Until = until;
        Status = status;
        this.options = options;
    }

    /// <summary><see cref="CursorStatus.Expired"/> when the log was trimmed past the cursor, so <see cref="Log"/> misses entries; resync from current state.</summary>
    public CursorStatus Status { get; }

    /// <summary>The version the cursor last committed; the window excludes it.</summary>
    public ulong Since { get; }

    /// <summary>The world version when the sample was taken; the window includes it. Pass it to <see cref="ChangeTracking.Commit"/> to consume the sample.</summary>
    public ulong Until { get; }

    /// <summary>The structural-log entries in the window that pass the cursor's <see cref="CursorOptions"/>, oldest first. An entry for a
    /// type is skipped when its entity is pending destroy or detached as the entry is read, unless the options include it.</summary>
    public LogSpan Log => new(world, Since, Until, options);

    /// <summary>The entities holding <typeparamref name="T"/> present whose value was stamped in the window, skipping an entity pending
    /// destroy or detached unless the cursor's options include it; removals and disables are not seen (the log has them), an
    /// <see cref="UntrackedAttribute"/> type yields none, and a value stamped again after <see cref="Until"/> shows in the next sample
    /// instead.</summary>
    public ChangedValues<T> Changed<T>()
        where T : struct, IComponent =>
        new(world, world.registry.IndexOf<T>(), Since, Until, options);

    /// <summary>The rows of <typeparamref name="TRow"/> stamped in the window, each with its owner and key, as <see cref="Changed{T}"/>
    /// is for components: a row added in the window is included, an owner pending destroy or detached is skipped unless the cursor's
    /// options include it, removals are not seen (the log has them), an <see cref="UntrackedAttribute"/> type or a write inside
    /// <see cref="World.Untracked"/> stamps none, and a row stamped again after <see cref="Until"/> shows in the next sample instead.
    /// Only owners with a row stamped after <see cref="Since"/> are visited, and one walk of <typeparamref name="TRow"/> may be open at a
    /// time: starting another before it ends throws.</summary>
    public ChangedRows<TKey, TRow> ChangedRows<TKey, TRow>()
        where TKey : notnull
        where TRow : struct, IRow<TKey> => new(world, Since, Until, options);

    /// <summary>The key of a <see cref="LogKind.RowAdded"/> or <see cref="LogKind.RowRemoved"/> entry for <typeparamref name="TRow"/>.</summary>
    public TKey RowKey<TKey, TRow>(in LogEntry entry)
        where TKey : notnull
        where TRow : struct, IRow<TKey> =>
        world.FindRows<TKey, TRow>() is { } store && entry.TypeIndex == store.Type
            ? store.KeyAt(entry.Detail)
            : throw new ArgumentException(
                $"The entry is not a {typeof(TRow).Name} row entry.",
                nameof(entry)
            );
}

/// <summary>The filtered structural-log entries of one <see cref="ChangeSample"/>; enumerate it before the world logs anything more or a cursor commits, either of which can move the entries.</summary>
public readonly ref struct LogSpan
{
    readonly World world;
    readonly ulong since;
    readonly ulong until;
    readonly CursorOptions options;

    internal LogSpan(World world, ulong since, ulong until, CursorOptions options)
    {
        this.world = world;
        this.since = since;
        this.until = until;
        this.options = options;
    }

    /// <summary>Walks the entries oldest first.</summary>
    public Enumerator GetEnumerator() => new(world, since, until, options);

    /// <summary>Walks a <see cref="LogSpan"/>.</summary>
    public ref struct Enumerator
    {
        readonly World world;
        readonly StructuralLog log;
        readonly ulong until;
        readonly CursorOptions options;
        int position;

        internal Enumerator(World world, ulong since, ulong until, CursorOptions options)
        {
            this.world = world;
            log = world.changes.log;
            this.until = until;
            this.options = options;
            position = log.FirstAfter(since) - 1;
        }

        /// <summary>The entry at the current position.</summary>
        public readonly ref readonly LogEntry Current => ref log.At(position);

        /// <summary>Moves to the next entry in the window that passes the filters; false past the last.</summary>
        public bool MoveNext()
        {
            while (++position < log.End)
            {
                ref readonly var entry = ref log.At(position);
                if (entry.Version > until)
                    return false;
                if (Matches(entry))
                    return true;
            }
            return false;
        }

        readonly bool Matches(in LogEntry entry)
        {
            if (entry.TypeIndex < 0)
                return true;
            if (
                options.Components is { Length: > 0 } components
                && Array.IndexOf(components, entry.TypeIndex) < 0
            )
                return false;
            return options.Categories.Matches(world.registry[entry.TypeIndex].Categories)
                && world.Admits(entry.Entity, options);
        }
    }
}

/// <summary>One entity whose value of <typeparamref name="T"/> was stamped inside a sample's window.</summary>
public readonly ref struct ChangedValue<T>
    where T : struct
{
    readonly ref readonly T value;

    internal ChangedValue(Entity entity, ref readonly T value, bool added, ulong version)
    {
        Entity = entity;
        this.value = ref value;
        Added = added;
        Version = version;
    }

    /// <summary>The entity holding the value.</summary>
    public Entity Entity { get; }

    /// <summary>The entity's current value, by reference into storage; valid until the next structural change.</summary>
    public ref readonly T Value => ref value;

    /// <summary>The component itself arrived inside the window, rather than only its value changing.</summary>
    public bool Added { get; }

    /// <summary>The version the value was last stamped at.</summary>
    public ulong Version { get; }
}

/// <summary>Values of <typeparamref name="T"/> stamped inside a version window; chunks whose newest stamp is older are skipped whole.</summary>
public readonly ref struct ChangedValues<T>
    where T : struct
{
    readonly World world;
    readonly int type;
    readonly ulong since;
    readonly ulong until;
    readonly CursorOptions options;

    internal ChangedValues(World world, int type, ulong since, ulong until, CursorOptions options)
    {
        this.world = world;
        this.type = type;
        this.since = since;
        this.until = until;
        this.options = options;
    }

    /// <summary>Walks the stamped values in storage order.</summary>
    public Enumerator GetEnumerator() => new(world, type, since, until, options);

    /// <summary>Walks a <see cref="ChangedValues{T}"/>.</summary>
    public ref struct Enumerator
    {
        readonly World world;
        readonly int type;
        readonly ulong since;
        readonly ulong until;
        readonly CursorOptions options;
        readonly SparseStorage<T>? sparse;
        int archetype;
        int chunk;
        int row;
        int end;
        int tracked;
        int column;
        int enable;
        Archetype? current;
        Entity entity;
        ulong stamp;
        bool added;
        ref T value;

        internal Enumerator(World world, int type, ulong since, ulong until, CursorOptions options)
        {
            this.world = world;
            this.type = type;
            this.since = since;
            this.until = until;
            this.options = options;
            var sparseSlot = world.layout.SparseSlot[type];
            sparse = sparseSlot >= 0 ? (SparseStorage<T>?)world.sparse[sparseSlot] : null;
            archetype = -1;
            tracked = -1;
            column = -1;
            enable = -1;
            chunk = -1;
            row = -1;
        }

        /// <summary>The value at the current position.</summary>
        public readonly ChangedValue<T> Current => new(entity, ref value, added, stamp);

        /// <summary>Moves to the next value stamped inside the window; false past the last.</summary>
        public bool MoveNext()
        {
            if (sparse is not null)
            {
                while (++row < sparse.Count)
                {
                    stamp = sparse.Changed[row];
                    if (
                        stamp > since
                        && stamp <= until
                        && world.Admits(sparse.Owners[row], options)
                    )
                    {
                        entity = sparse.Owners[row];
                        added = sparse.Added[row] > since;
                        value = ref sparse.Values[row];
                        return true;
                    }
                }
                return false;
            }
            while (true)
            {
                if (current is not null)
                {
                    while (++row < end && row < current.Count)
                    {
                        stamp = current.ChangedAt(tracked, row);
                        if (
                            stamp <= since
                            || stamp > until
                            || (enable >= 0 && !current.IsEnabled(enable, row))
                            || (
                                !options.IncludePendingDestroy
                                && (current.IsPendingDestroy(row) || current.HasAncestor(0, row))
                            )
                        )
                            continue;
                        entity = current.Entities[row];
                        added = current.AddedAt(tracked, row) > since;
                        value = ref column >= 0
                            ? ref current.Values<T>(column)[row]
                            : ref TagValue<T>.Value;
                        return true;
                    }
                }
                if (!NextChunk())
                    return false;
            }
        }

        bool NextChunk()
        {
            var archetypes = world.archetypes;
            while (true)
            {
                if (tracked >= 0 && archetype >= 0)
                {
                    var a = archetypes[archetype];
                    while (++chunk < a.ChunkCount)
                    {
                        if (a.MaxChangedIn(chunk, tracked) <= since)
                            continue;
                        current = a;
                        row = (chunk * a.ChunkCapacity) - 1;
                        end = row + 1 + a.ChunkCapacity;
                        return true;
                    }
                }
                if (++archetype >= archetypes.Count)
                    return false;
                var next = archetypes[archetype];
                tracked =
                    next.Signature.Has(type) && (options.IncludeDetached || !next.Detached)
                        ? next.TrackedSlot(type)
                        : -1;
                column = tracked >= 0 ? next.ColumnSlot(type) : -1;
                enable = tracked >= 0 ? next.EnableSlot(type) : -1;
                chunk = -1;
                current = null;
            }
        }
    }
}

internal static class TagValue<T>
    where T : struct
{
    public static T Value;
}
