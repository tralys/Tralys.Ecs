using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>One chunk of a query run: up to 64 consecutive rows of one archetype. <see cref="Rows"/> lists the rows the query visits, as
/// indices into the spans; rows filtered out by the query are skipped.</summary>
public ref struct ChunkView
{
    internal readonly World world;
    readonly CachedQuery query;
    readonly Archetype archetype;
    readonly ArchetypeMatch match;
    readonly int start;
    readonly CommandBuffer? buffer;
    readonly ulong version;
    readonly bool tracking;
    internal bool stopped;

    internal ChunkView(
        CachedQuery query,
        scoped in Matched entry,
        int start,
        int count,
        ulong rows,
        int chunkIndex,
        int worker,
        CommandBuffer? buffer
    )
    {
        world = query.world;
        this.query = query;
        archetype = entry.Archetype;
        match = entry.Match;
        this.start = start;
        this.buffer = buffer;
        version = world.version;
        tracking = world.Tracking;
        Count = count;
        RowMask = rows;
        ChunkIndexInQuery = chunkIndex;
        Worker = worker;
    }

    /// <summary>The reads a kernel may make of the world the query runs on.</summary>
    public readonly WorldReader Reader => new(world);

    /// <summary>Rows in the chunk, visited or not; the length of its spans.</summary>
    public int Count { get; }

    /// <summary>The rows the query visits. Write <c>foreach (var row in chunk.Rows)</c>, not a loop to <see cref="Count"/>: a row whose entity
    /// the walk has since recorded out of the query is skipped.</summary>
    public readonly RowSet Rows => new(RowMask, Filter);

    internal ulong RowMask { get; }

    readonly RowFilter Filter => new(query, archetype, match, start);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly bool Visits(int row) => !Filter.Skips(row);

    /// <summary>The chunk's position in this run, counting chunks the query skipped. Commands recorded in a partitioned run play back in this order.</summary>
    public int ChunkIndexInQuery { get; }

    /// <summary>The executor worker running the chunk; 0 in a serial run.</summary>
    public int Worker { get; }

    /// <summary>The chunk's entities, one per row, visited or not.</summary>
    public readonly ReadOnlySpan<Entity> Entities => archetype.Entities.AsSpan(start, Count);

    /// <summary>This worker's command buffer in a partitioned run; a serial run changes structure through the world, so it has none.</summary>
    /// <exception cref="InvalidOperationException">The run is serial: change structure through the world, which records the change until
    /// the walk closes.</exception>
    public readonly Commands Commands => (buffer ?? throw SerialCommands()).Commands;

    static InvalidOperationException SerialCommands() =>
        new(
            "A serial run has no Commands; change structure through the World, which records the change until the walk closes."
        );

    /// <summary>The chunk's position and worker.</summary>
    public readonly ChunkContext Context => new(world, ChunkIndexInQuery, Worker);

    /// <summary>Ends a serial run after this chunk.</summary>
    /// <exception cref="InvalidOperationException">The run is parallel; which chunks had run would depend on the workers.</exception>
    public void Stop()
    {
        if (buffer is not null)
            throw new InvalidOperationException(
                "A parallel run cannot stop early, since which chunks had run would depend on the workers; filter the rows or make the query serial."
            );
        stopped = true;
    }

    /// <summary>Whether the chunk's archetype holds <typeparamref name="T"/>, enabled or not; false for a sparse type.</summary>
    public readonly bool Has<T>()
        where T : struct, IComponent => archetype.Signature.Has(ComponentType<T>.Index);

    /// <summary>The column of <typeparamref name="T"/> for reading, as stored: a value recorded for a row's entity since is read through the
    /// world. Throws <see cref="InvalidOperationException"/> when the archetype lacks it.</summary>
    public readonly ReadOnlySpan<T> Read<T>()
        where T : struct, IComponent => ColumnOf<T>();

    /// <summary>The column of <typeparamref name="T"/> for writing through <see cref="TrackedSpan{T}.Edit"/>, which stamps a row and moves
    /// its index entries only if its value changed. The query must declare <typeparamref name="T"/> with <c>Write</c> or
    /// <c>OptionalWrite</c>. It holds stored values: a value recorded for a row's entity since is written through the world.</summary>
    public readonly TrackedSpan<T> Write<T>()
        where T : struct, IMutableComponent
    {
        CheckDeclaredWrite<T>();
        return Tracked<T>(ColumnOf<T>(), archetype.TrackedSlot(ComponentType<T>.Index), true);
    }

    /// <summary>Writes that stamp nothing; consumers never see them as changes. Not for indexed or buffer-owning types, whose writes must be
    /// released through <see cref="Write{T}"/>.</summary>
    public readonly Span<T> WriteUntracked<T>()
        where T : struct, IMutableComponent
    {
        CheckDeclaredWrite<T>();
        var type = ComponentType<T>.Index;
        if (world.layout.IndexBase[type] >= 0 || world.layout.OwnsBuffers[type])
            throw new InvalidOperationException(
                $"{typeof(T).Name} is indexed or owns buffers, so an untracked write would skip its index or leak a buffer; write it with Write<{typeof(T).Name}>()."
            );
        return ColumnOf<T>();
    }

    /// <summary>Whether <paramref name="row"/> holds <typeparamref name="T"/> enabled: false when the archetype lacks it, true when it is not enableable.</summary>
    public readonly bool IsEnabled<T>(int row)
        where T : struct, IComponent
    {
        var at = archetype.IndexOf(ComponentType<T>.Index);
        if (at < 0)
            return false;
        var enable = archetype.SlotAt(at).Enable;
        return enable < 0 || archetype.IsEnabled(enable, start + row);
    }

    /// <summary>The world's sparse storage of <typeparamref name="T"/>; throws <see cref="InvalidOperationException"/> when it is not stored sparse.</summary>
    public readonly SparseTerm<T> Sparse<T>()
        where T : struct, IComponent => SparseOf<T>(world.registry.IndexOf<T>());

    internal readonly Span<T> Column<T>(int term)
    {
        var column = match.Column[term];
        return column < 0 ? default : archetype.Values<T>(column).AsSpan(start, Count);
    }

    internal readonly TrackedSpan<T> Tracked<T>(int term)
        where T : struct, IMutableComponent =>
        Tracked<T>(Column<T>(term), match.Tracked[term], true);

    internal readonly TrackedSpan<T> StampOnly<T>(int term)
        where T : struct, IMutableComponent
    {
        var type = query.shape.Types[term];
        var layout = world.layout;
        if (
            layout.IndexBase[type] >= 0
            || layout.PropagatesToAncestors[type]
            || layout.PropagatesToSources[type]
            || layout.OwnsBuffers[type]
        )
            ThrowNotStampOnly(typeof(T).Name);
        return Tracked<T>(Column<T>(term), match.Tracked[term], false);
    }

    internal readonly bool HasRow(int term, int row)
    {
        if (!match.Present[term])
            return false;
        var enable = match.Enable[term];
        return enable < 0 || archetype.IsEnabled(enable, start + row);
    }

    internal readonly SparseTerm<T> Sparse<T>(int term)
        where T : struct, IComponent => SparseOf<T>(query.shape.Types[term]);

    internal readonly bool IsPending(int row) => archetype.IsPending(start + row);

    internal readonly int Overlay(int term, int row) =>
        world.overlay.TryGet(
            archetype.Entities[start + row].Index,
            query.shape.Types[term],
            out _,
            out var enabled,
            out _
        )
            ? enabled
                ? 1
                : 0
            : -1;

    internal readonly ref T Overlaid<T>(int term, int row, out bool recorded, out int slot)
        where T : struct
    {
        ref var value = ref world.FindSlow<T>(
            archetype.Entities[start + row],
            query.shape.Types[term],
            out var location
        );
        recorded = location.Kind == LocationKind.Overlay;
        slot = location.Kind == LocationKind.Sparse ? location.Row : -1;
        return ref value;
    }

    internal readonly void Collect(int row) => query.AddToSnapshot(Entities[row]);

    readonly Span<T> ColumnOf<T>()
        where T : struct
    {
        var type = ComponentType<T>.Index;
        var column = archetype.Map.ColumnOf(type);
        if (column < 0)
            throw new InvalidOperationException(
                world.layout.Class[type] == StorageClass.Sparse
                    ? $"{typeof(T).Name} is sparse, so no chunk has a column for it; read it through Sparse<{typeof(T).Name}>()."
                    : $"This chunk's archetype has no {typeof(T).Name} column."
            );
        return archetype.Values<T>(column).AsSpan(start, Count);
    }

    readonly TrackedSpan<T> Tracked<T>(Span<T> values, int tracked, bool maintainsIndexes)
        where T : struct, IMutableComponent =>
        new(
            values,
            archetype,
            start,
            tracking || buffer is null ? tracked : -1,
            version,
            buffer is null ? world : null,
            maintainsIndexes ? IndexesOf(ComponentType<T>.Index) : null,
            BuffersOf(ComponentType<T>.Index),
            buffer
        );

    readonly SparseTerm<T> SparseOf<T>(int type)
        where T : struct, IComponent
    {
        var slot = world.layout.SparseSlot[type];
        if (slot < 0)
            throw new InvalidOperationException($"{typeof(T).Name} is not stored sparse.");
        var storage = Unsafe.As<SparseStorage<T>>(world.sparse[slot]);
        return new SparseTerm<T>(
            storage,
            (tracking || buffer is null) && world.registry[type].IsStamped ? version : 0,
            buffer is null ? world : null,
            IndexesOf(type),
            BuffersOf(type),
            buffer
        );
    }

    readonly World? IndexesOf(int type) => world.layout.IndexBase[type] >= 0 ? world : null;

    readonly World? BuffersOf(int type) => world.layout.OwnsBuffers[type] ? world : null;

    static void ThrowNotStampOnly(string name) =>
        throw new InvalidOperationException(
            $"{name} is indexed or propagates changes, but this query was generated for a type that does neither; rebuild it."
        );

    readonly void CheckDeclaredWrite<T>()
        where T : struct
    {
        var type = ComponentType<T>.Index;
        foreach (var term in query.desc.Terms)
        {
            if (term.Write && term.Key.Index == type)
                return;
        }
        throw new InvalidOperationException(
            $"The query does not declare a write of {typeof(T).Name}; build it with Write<{typeof(T).Name}>() or OptionalWrite<{typeof(T).Name}>()."
        );
    }
}

/// <summary>Where a query body is running: the chunk's position and the worker running it.</summary>
public readonly ref struct ChunkContext
{
    readonly World world;

    internal ChunkContext(World world, int chunkIndex, int worker)
    {
        this.world = world;
        ChunkIndex = chunkIndex;
        Worker = worker;
    }

    /// <summary>The reads a body may make of the world the query runs on.</summary>
    public WorldReader Reader => new(world);

    /// <summary>The chunk's position in the run, counting chunks the query skipped.</summary>
    public int ChunkIndex { get; }

    /// <summary>The executor worker running the chunk; 0 in a serial run.</summary>
    public int Worker { get; }
}

/// <summary>A chunk's column of <typeparamref name="T"/>. The indexer reads; <see cref="Edit"/> writes one row and, on dispose, stamps it
/// and moves its index entries only if its value changed.</summary>
[Borrow]
public readonly ref struct TrackedSpan<T>
    where T : struct, IMutableComponent
{
    readonly Span<T> values;
    readonly Archetype archetype;
    readonly int start;
    readonly int tracked;
    readonly ulong version;
    readonly World? serial;
    readonly World? indexes;
    readonly World? buffers;
    readonly CommandBuffer? buffer;

    internal TrackedSpan(
        Span<T> values,
        Archetype archetype,
        int start,
        int tracked,
        ulong version,
        World? serial,
        World? indexes,
        World? buffers,
        CommandBuffer? buffer
    )
    {
        this.values = values;
        this.archetype = archetype;
        this.start = start;
        this.tracked = tracked;
        this.version = version;
        this.serial = serial;
        this.indexes = indexes;
        this.buffers = buffers;
        this.buffer = buffer;
    }

    /// <summary>Rows in the chunk, visited or not.</summary>
    public int Length => values.Length;

    /// <summary>Whether writes through this span are stamped: the type is tracked and no untracked scope is open.</summary>
    public bool Tracks => tracked >= 0 && (serial is null || serial.Tracking);

    // A serial body can advance the version mid-chunk, so it stamps the version current at release.
    ulong Version => serial?.version ?? version;

    /// <summary>The value at <paramref name="row"/>, read-only.</summary>
    public ref readonly T this[int row] => ref values[row];

    /// <summary>A write to <paramref name="row"/>; dispose it to stamp the row and move its index entries if the value changed.</summary>
    public ColumnEdit<T> Edit(int row) => new(this, row);

    internal bool Compares => tracked >= 0 || indexes is not null || buffers is not null;

    internal ref T Unstamped(int row) => ref values[row];

    internal void StampIfChanged(int row, in T before)
    {
        if (
            !ComponentOpsOf<T>.Equals(
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in before)),
                ref Unsafe.As<T, byte>(ref values[row])
            )
        )
            archetype.StampOnly(tracked, start + row, Version);
    }

    internal void Release(int row, in T before)
    {
        if (
            !ComponentOpsOf<T>.Equals(
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in before)),
                ref Unsafe.As<T, byte>(ref values[row])
            )
        )
            Changed(row, in before);
    }

    // A partitioned run records the index move for the join.
    internal void Changed(int row, in T before)
    {
        ref var value = ref values[row];
        buffers?.ReleaseBuffers(ComponentType<T>.Index, in before, in value, buffer is not null);
        if (Tracks)
            archetype.Stamp(tracked, start + row, Version);
        indexes?.IndexWritten(
            archetype.Entities[start + row],
            ComponentType<T>.Index,
            buffer,
            in before,
            in value
        );
    }
}

/// <summary>A write to one row of a <see cref="TrackedSpan{T}"/>.</summary>
[MustDispose("its change stamp and index update are lost")]
[Borrow]
public ref struct ColumnEdit<T>
    where T : struct, IMutableComponent
{
    readonly TrackedSpan<T> span;
    readonly int row;
    readonly T before;
    bool disposed;

    internal ColumnEdit(TrackedSpan<T> span, int row)
    {
        this.span = span;
        this.row = row;
        before = span.Compares ? span.Unstamped(row) : default;
    }

    /// <summary>The row's value, writable until the edit is disposed.</summary>
    public readonly ref T Value => ref span.Unstamped(row);

    /// <summary>Stamps the row, moves its index entries and frees replaced buffers if its value changed; a second dispose does nothing.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (span.Compares)
            span.Release(row, in before);
    }
}

/// <summary>Sparse storage of <typeparamref name="T"/> seen from a query body. <see cref="Find"/> reads; <see cref="Edit"/> writes and, on
/// dispose, stamps and moves index entries only if the value changed.</summary>
public readonly ref struct SparseTerm<T>
    where T : struct, IComponent
{
    readonly SparseStorage<T>? storage;
    readonly ulong version;
    readonly World? serial;
    readonly World? indexes;
    readonly World? buffers;
    readonly CommandBuffer? buffer;

    internal SparseTerm(
        SparseStorage<T>? storage,
        ulong version,
        World? serial,
        World? indexes,
        World? buffers,
        CommandBuffer? buffer
    )
    {
        this.storage = storage;
        this.version = version;
        this.serial = serial;
        this.indexes = indexes;
        this.buffers = buffers;
        this.buffer = buffer;
    }

    /// <summary>Whether writes are stamped: the type is tracked and no untracked scope is open.</summary>
    public bool Tracks => version != 0 && (serial is null || serial.Tracking);

    /// <summary>The entity's value by <c>ref readonly</c>, or an empty optional when it has none.</summary>
    public Optional<T> Find(Entity entity) => new(in FindSlot(entity, out _));

    /// <summary>A write to the entity's value, or one without a value when it has none.</summary>
    public SparseEdit<T> Edit(Entity entity)
    {
        ref var value = ref FindSlot(entity, out var slot);
        return new SparseEdit<T>(this, ref value, slot);
    }

    internal bool Compares => version != 0 || indexes is not null || buffers is not null;

    internal ref T FindSlot(Entity entity, out int slot)
    {
        if (storage is null || storage.SlotOf(entity.Index) is not (var at and >= 0))
        {
            slot = -1;
            return ref Unsafe.NullRef<T>();
        }
        slot = at;
        return ref storage.Values[at];
    }

    internal void Release(int slot, in T before)
    {
        if (storage is null)
            return;
        ref var value = ref storage.Values[slot];
        if (
            ComponentOpsOf<T>.Equals(
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in before)),
                ref Unsafe.As<T, byte>(ref value)
            )
        )
            return;
        buffers?.ReleaseBuffers(storage.Type, in before, in value, buffer is not null);
        if (Tracks)
            storage.Stamp(slot, serial?.version ?? version);
        indexes?.IndexWritten(storage.Owners[slot], storage.Type, buffer, in before, in value);
    }
}

/// <summary>A write to one entity's value of a <see cref="SparseTerm{T}"/>.</summary>
[MustDispose("its change stamp and index update are lost")]
[Borrow]
public ref struct SparseEdit<T>
    where T : struct, IComponent
{
    readonly SparseTerm<T> term;
    readonly ref T value;
    readonly int slot;
    readonly T before;
    bool disposed;

    internal SparseEdit(SparseTerm<T> term, ref T value, int slot)
    {
        this.term = term;
        this.value = ref value;
        this.slot = slot;
        before = slot >= 0 && term.Compares ? value : default;
    }

    /// <summary>Whether the entity holds a value to edit.</summary>
    public readonly bool HasValue => slot >= 0;

    /// <summary>The entity's value, writable until the edit is disposed; throws <see cref="InvalidOperationException"/> when <see cref="HasValue"/> is false.</summary>
    public readonly ref T Value
    {
        get
        {
            if (slot < 0)
                Optional<T>.ThrowEmpty();
            return ref value;
        }
    }

    /// <summary>Stamps the value, moves its index entries and frees replaced buffers if it changed; a second dispose does nothing.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (slot >= 0 && term.Compares)
            term.Release(slot, in before);
    }
}

/// <summary>An optional query term: <see cref="HasValue"/> is false when the entity lacks the component or has it disabled.</summary>
[Borrow]
public readonly ref struct Optional<T>
    where T : struct, IDeclared
{
    readonly ref readonly T value;
    readonly BorrowCheck check;

    internal Optional(ref readonly T value) => this.value = ref value;

    internal Optional(ref readonly T value, BorrowCheck check)
    {
        this.value = ref value;
        this.check = check;
    }

    /// <summary>Whether the term has a value.</summary>
    public bool HasValue => !Unsafe.IsNullRef(in value);

    /// <summary>The value by <c>ref readonly</c>.</summary>
    /// <exception cref="InvalidOperationException"><see cref="HasValue"/> is false, or, with <see cref="WorldOptions.CheckBorrows"/>, the
    /// storage the value lives in moved since it was found.</exception>
    public ref readonly T Value
    {
        get
        {
            if (Unsafe.IsNullRef(in value))
                ThrowEmpty();
            check.Verify();
            return ref value;
        }
    }

    /// <summary>A copy of the value, or <c>default</c> when there is none.</summary>
    /// <exception cref="InvalidOperationException">With <see cref="WorldOptions.CheckBorrows"/>, the storage the value lives in moved
    /// since it was found.</exception>
    public T GetValueOrDefault()
    {
        if (!HasValue)
            return default;
        check.Verify();
        return value;
    }

    internal static void ThrowEmpty() =>
        throw new InvalidOperationException($"The optional {typeof(T).Name} has no value.");
}

internal interface IMoveCount
{
    int MoveCount { get; }
}

internal readonly struct BorrowCheck
{
    readonly IMoveCount? storage;
    readonly int moves;

    BorrowCheck(IMoveCount storage)
    {
        this.storage = storage;
        moves = storage.MoveCount;
    }

    public static BorrowCheck Of(IMoveCount? storage) => storage is null ? default : new(storage);

    public void Verify()
    {
        if (storage is not null && storage.MoveCount != moves)
            throw new InvalidOperationException(
                "The storage this optional points into moved since it was found, so it may read another entity or row; find it again."
            );
    }
}

/// <summary>A writable optional query term. Generated queries stamp the row on release only if the value changed.</summary>
[Borrow]
public readonly ref struct OptionalRef<T>
    where T : struct, IMutableComponent
{
    readonly ref T value;

    internal OptionalRef(ref T value) => this.value = ref value;

    /// <summary>Whether the term has a value.</summary>
    public bool HasValue => !Unsafe.IsNullRef(ref value);

    /// <summary>The value by <c>ref</c>; throws <see cref="InvalidOperationException"/> when <see cref="HasValue"/> is false.</summary>
    public ref T Value
    {
        get
        {
            if (Unsafe.IsNullRef(ref value))
                Optional<T>.ThrowEmpty();
            return ref value;
        }
    }
}
