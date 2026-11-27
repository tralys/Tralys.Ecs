using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>Settings for <c>World.Create</c>; <c>default</c> gives every documented default and no debug checks.</summary>
public readonly record struct WorldOptions
{
    /// <summary>Rows per chunk; a power of two up to 64. Zero means 32.</summary>
    public int ChunkCapacity { get; init; }

    /// <summary>Runs partitioned queries. Null runs them on the calling thread in chunk order.</summary>
    public IChunkExecutor? Executor { get; init; }

    /// <summary>Changes each <see cref="ChangeWatch{TBits}"/> holds between collects; past it the watch drops them and marks everything at
    /// its next collect. Zero means 1,048,576.</summary>
    public int WatchQueueCap { get; init; }

    /// <summary>Throws when the world API changes the world from a thread other than its owner: the creating thread, or the last to call
    /// <see cref="World.BindToCurrentThread"/>. The check runs only when this is set, in debug and release builds alike. For tests and
    /// debug runs: every write reads the current thread's id.</summary>
    public bool CheckThread { get; init; }

    /// <summary>Keeps this many of the newest structural-log entries, each with the systems and source line behind it, in
    /// <see cref="World.AttributedLog"/>. Zero keeps none. For tests and debug runs: every write records its call site.</summary>
    public int AttributedLogCapacity { get; init; }

    /// <summary>After every playback, walks each entity reference stored in components, relation payloads and rows, entity buffers
    /// included, and reports those naming a dead entity to <see cref="World.ReferenceFaults"/>. For tests and debug runs: the walk visits
    /// every stored value.</summary>
    public bool VerifyReferences { get; init; }

    /// <summary>After every playback and every immediate destroy, cancel, reparent, edge write, detach or reattach, checks the world's
    /// structural invariants and throws <see cref="InvalidOperationException"/> at the first one broken: no live source holds a
    /// <see cref="TargetPolicy.Throw"/> edge onto a dying or released target, each entity's partition and pending-destroy bits follow its
    /// own flags and its parent's, and a finalisation releases exactly the entities it planned to. For tests and debug runs: each check
    /// walks every entity and edge.</summary>
    public bool VerifyInvariants { get; init; }

    /// <summary>Makes each read through an <see cref="Optional{T}"/> from <see cref="World.Find{T}"/> or <c>Rows.Find</c> throw once the
    /// storage it points into has moved. Null turns it on when a module of the registry was compiled with <c>DEBUG</c> defined. For tests and
    /// debug runs: every such read compares a move counter.</summary>
    public bool? CheckBorrows { get; init; }
}

/// <summary>A set of entities and their components. Not thread-safe: one owner thread mutates it.</summary>
public sealed partial class World : IDisposable, IMoveCount
{
    internal readonly Registry registry;
    internal readonly RegistryLayout layout;
    internal readonly EntityAllocator allocator = new();
    internal readonly List<Archetype> archetypes = [];
    readonly Dictionary<ArchetypeSignature, Archetype> bySignature = [];
    internal readonly Archetype root;
    internal readonly SparseStorage?[] sparse;
    internal readonly SparseTagStorage?[] sparseTags;
    readonly int chunkCapacity;
    internal readonly IChunkExecutor executor;

    [Effect(Effects.MovesEntities)]
    internal int structuralCount;
    internal int parallelDepth;
    internal readonly bool checkBorrows;
    CachedQuery?[] queries = [];
    internal readonly List<CachedQuery> liveQueries = [];

    World(Registry registry, in WorldOptions options)
    {
        this.registry = registry;
        layout = registry.Layout;
        chunkCapacity = options.ChunkCapacity == 0 ? 32 : options.ChunkCapacity;
        // A query chunk's row mask is one 64-bit word.
        if (!int.IsPow2(chunkCapacity) || chunkCapacity > 64)
            throw new ArgumentException(
                "ChunkCapacity must be a power of two no larger than 64.",
                nameof(options)
            );
        executor = options.Executor ?? InlineExecutor.Instance;
        checkThread = options.CheckThread;
        sparse = new SparseStorage?[layout.SparseCount];
        sparseTags = new SparseTagStorage?[layout.SparseTagCount];
        relations = new RelationStore?[layout.RelationCount];
        guardsTargets = HasThrowRelation();
        rowStores = new RowStore?[layout.RowCount];
        singletonHolders = new Entity[layout.SingletonCount];
        indexStores = new IndexStore?[layout.IndexSlotType.Length];
        hasIndexes = layout.IndexedTypes.Length > 0;
        if (options.AttributedLogCapacity < 0)
            throw new ArgumentException(
                "AttributedLogCapacity cannot be negative.",
                nameof(options)
            );
        if (options.AttributedLogCapacity > 0)
            attributed = new AttributedLog(options.AttributedLogCapacity);
        verifyReferences = options.VerifyReferences;
        verifyInvariants = options.VerifyInvariants;
        checkBorrows = options.CheckBorrows ?? !registry.Optimized;
        root = ArchetypeFor(ArchetypeSignature.Empty(registry.IndexBound));
        changes = new ChangeTracking(
            this,
            options.WatchQueueCap == 0 ? 1 << 20 : options.WatchQueueCap,
            attributed
        );
        hierarchy.Raises = changes;
        observers = new ObserverRegistry(this);
        stage = new CommandBuffer(this, parallel: false);
        buffersById.Add(stage);
        overlay = new PendingOverlay();
        ownerThread = Environment.CurrentManagedThreadId;
    }

    /// <summary>An empty world over <paramref name="registry"/>, owned by the calling thread.</summary>
    /// <exception cref="ArgumentException"><see cref="WorldOptions.ChunkCapacity"/> is neither zero nor a power of two up to 64, or
    /// <see cref="WorldOptions.AttributedLogCapacity"/> is negative.</exception>
    [Effect(Effects.None)]
    public static World Create(Registry registry, in WorldOptions options = default) =>
        new(registry, options);

    /// <summary>The component, relation and row types this world was created over.</summary>
    public Registry Registry => registry;

    /// <summary>Receives per-entity faults from collect-then-act runs. Null lets them propagate.</summary>
    public IFaultSink? Faults { get; set; }

    /// <summary>This world's cached instance of <paramref name="desc"/>, created on first use.</summary>
    [Effect(Effects.None)]
    public CachedQuery Query(QueryDesc desc)
    {
        var id = desc.Id;
        if ((uint)id < (uint)queries.Length && queries[id] is { } cached)
            return cached;
        return CreateQuery(desc);
    }

    CachedQuery CreateQuery(QueryDesc desc)
    {
        if (desc.Id >= queries.Length)
            Array.Resize(ref queries, Math.Max(desc.Id + 1, queries.Length * 2));
        var query = new CachedQuery(this, desc);
        queries[desc.Id] = query;
        liveQueries.Add(query);
        return query;
    }

    /// <summary>Drops every archetype's rows and chunk storage; do not use the world afterwards. Throws while a query iterates or
    /// observers run.</summary>
    [Effect(Effects.MovesEntities | Effects.Mutates | Effects.Settled)]
    public void Dispose()
    {
        CheckSerial();
        CheckNotIterating(nameof(Dispose));
        structuralCount++;
        foreach (var archetype in archetypes)
        {
            archetype.Release();
            archetype.TrimStorage();
        }
    }

    internal Archetype ArchetypeFor(in ArchetypeSignature signature)
    {
        if (bySignature.TryGetValue(signature, out var existing))
            return existing;
        var archetype = new Archetype(archetypes.Count, signature, registry, chunkCapacity, this);
        archetypes.Add(archetype);
        bySignature.Add(signature, archetype);
        partitionsUsed |= signature.Detached;
        foreach (var query in liveQueries)
            query.OnArchetype(archetype);
        return archetype;
    }

    // The words are the caller's scratch: a new archetype keeps a copy.
    internal Archetype ArchetypeFor(ulong[] words, bool detached) =>
        bySignature.TryGetValue(new ArchetypeSignature(words, detached), out var existing)
            ? existing
            : ArchetypeFor(new ArchetypeSignature((ulong[])words.Clone(), detached));

    internal Archetype WithType(Archetype source, int type)
    {
        if (source.AddEdges.TryGetValue(type, out var target))
            return target;
        target = ArchetypeFor(source.Signature.With(type));
        source.AddEdges[type] = target;
        target.RemoveEdges[type] = source;
        return target;
    }

    internal Archetype WithoutType(Archetype source, int type)
    {
        if (source.RemoveEdges.TryGetValue(type, out var target))
            return target;
        target = ArchetypeFor(source.Signature.Without(type));
        source.RemoveEdges[type] = target;
        target.AddEdges[type] = source;
        return target;
    }

    internal Archetype Partitioned(Archetype source, bool detached)
    {
        if (source.Detached == detached)
            return source;
        if (source.PartitionEdge is { } target)
            return target;
        target = ArchetypeFor(source.Signature.WithDetached(detached));
        source.PartitionEdge = target;
        target.PartitionEdge = source;
        return target;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref EntitySlot Slot(Entity entity)
    {
        ref var slot = ref allocator.Current(entity);
        if (Unsafe.IsNullRef(ref slot))
            ThrowNotAlive(entity);
        return ref slot;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowNotAlive(Entity entity) => throw NotAlive(entity);

    internal static InvalidOperationException NotAlive(Entity entity) =>
        new($"{entity} does not exist in this world.");

    /// <summary>The handle names an entity of this world in any state but <see cref="LifecycleState.Dead"/>, including one pending destroy.</summary>
    [Effect(Effects.None)]
    public bool Exists(Entity entity) => allocator.IsCurrent(entity);

    /// <summary>How many workers a partitioned query on this world runs on: the <see cref="WorldOptions.Executor"/>'s
    /// <see cref="IChunkExecutor.WorkerCount"/>, or 1 without one. Every <see cref="ChunkContext.Worker"/> is below it, so it sizes per-worker
    /// scratch.</summary>
    public int WorkerCount => executor.WorkerCount;

    /// <summary>How many handles <see cref="Exists"/> holds true for: reserved, detached and pending-destroy entities count, dead slots do
    /// not. Constant time.</summary>
    public int EntityCount => allocator.InUse;

    /// <summary>The entity exists and neither it nor an ancestor is pending destroy. Reserved and detached entities are live.</summary>
    [Effect(Effects.None)]
    public bool IsLive(Entity entity) =>
        allocator.IsCurrent(entity)
        && allocator.Slots[entity.Index].State != LifecycleState.PendingDestroy
        && !UnderPendingDestroy(entity);

    /// <summary>The entity's lifecycle state. An entity under a pending-destroy ancestor is <see cref="LifecycleState.PendingDestroy"/>,
    /// since it goes with that ancestor.</summary>
    [Effect(Effects.None)]
    public LifecycleState StateOf(Entity entity)
    {
        if (!allocator.IsCurrent(entity))
            return LifecycleState.Dead;
        var state = allocator.Slots[entity.Index].State;
        return state == LifecycleState.Alive && UnderPendingDestroy(entity)
            ? LifecycleState.PendingDestroy
            : state;
    }

    /// <summary>The entity lives in the detached partition: reachable by handle, excluded from queries and systems.</summary>
    [Effect(Effects.None)]
    public bool IsDetached(Entity entity) =>
        allocator.IsCurrent(entity) && allocator.Slots[entity.Index].Detached;

    internal bool Admits(Entity entity, in CursorOptions options) =>
        (options.IncludePendingDestroy || StateOf(entity) != LifecycleState.PendingDestroy)
        && (options.IncludeDetached || !IsDetached(entity));

    internal void Place(Entity entity, ref EntitySlot slot, Archetype target)
    {
        var row = target.AllocateRow(entity);
        target.ClearStamps(row);
        target.SetPending(row, slot.Pending);
        slot.Archetype = target;
        slot.Row = row;
        slot.Detached = target.Detached;
        structuralCount++;
        if (!hierarchy.IsEmpty)
            InheritFromParent(entity);
    }

    internal void MoveTo(Entity entity, ref EntitySlot slot, Archetype target)
    {
        var source = slot.Placed;
        if (source == target)
            return;
        var map = source.MapTo(target);
        var fromRow = slot.Row;
        var row = target.AllocateRow(entity);

        var columns = map.Columns;
        var ops = target.ColumnOps;
        var sizes = target.ColumnSizes;
        var from = source.Columns;
        var to = target.Columns;
        for (var i = 0; i < columns.Length; i += 2)
        {
            var d = columns[i + 1];
            if (ops[d].Managed)
                ops[d].CopyManaged(from[columns[i]], fromRow, to[d], row);
            else
                RowBytes.Copy(from[columns[i]], fromRow, to[d], row, sizes[d]);
        }
        var enables = map.Enables;
        for (var i = 0; i < enables.Length; i += 2)
            target.SetEnabled(enables[i + 1], row, source.IsEnabled(enables[i], fromRow));
        var tracked = map.Tracked;
        for (var i = 0; i < tracked.Length; i += 2)
            target.CopyStamps(tracked[i + 1], row, source, tracked[i], fromRow);
        foreach (var t in map.FreshTracked)
            target.ClearStamps(t, row);
        target.SetPendingDestroy(row, source.IsPendingDestroy(fromRow));
        target.SetPending(row, source.IsPending(fromRow));
        if (hierarchy.EverUsed)
            target.CopyAncestors(row, source, fromRow);

        Vacate(source, fromRow);
        slot.Archetype = target;
        slot.Row = row;
        slot.Detached = target.Detached;
        structuralCount++;
        if (hierarchy.EverUsed && hierarchy.HasChildren(entity))
            RefreshChildren(entity);
    }

    int IMoveCount.MoveCount => structuralCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void MarkPending(ref EntitySlot slot)
    {
        slot.Pending = true;
        slot.Archetype?.SetPending(slot.Row, true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ClearPending(ref EntitySlot slot)
    {
        slot.Pending = false;
        slot.Archetype?.SetPending(slot.Row, false);
    }

    internal void Vacate(Archetype archetype, int row)
    {
        if (archetype.RemoveRow(row) is { } moved)
            allocator.Slots[moved.Index].Row = row;
        structuralCount++;
    }

    /// <summary>Releases the chunk storage of every empty archetype, which otherwise keeps the capacity it grew to so that entities
    /// passing through it allocate nothing; the next entity to enter one regrows it. Throws while a query iterates or observers run.</summary>
    [Effect(Effects.Mutates | Effects.Settled)]
    public void TrimExcess()
    {
        CheckWritable();
        CheckNotIterating(nameof(TrimExcess));
        foreach (var archetype in archetypes)
        {
            if (archetype.Count == 0 && archetype.Capacity > 0 && archetype != root)
                archetype.TrimStorage();
        }
    }

    [Effect(Effects.Settled)]
    void CheckNotIterating(string operation)
    {
        if (iterationDepth > 0)
            throw new InvalidOperationException(
                $"{operation} needs a settled world, and a query is iterating or observers are running; call it after the run."
            );
    }
}
