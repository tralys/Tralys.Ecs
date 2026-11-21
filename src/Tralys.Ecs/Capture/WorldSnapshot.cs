namespace Tralys.Ecs;

/// <summary>A whole-world image taken by <see cref="World.Snapshot()"/>: entities with their handles and generations, components, relations,
/// hierarchy, rows, buffers, indexes, change versions and allocator state. <see cref="World.Reset"/> rewinds the world it came from to it,
/// as often as needed.</summary>
public sealed class WorldSnapshot
{
    internal WorldSnapshot(
        World owner,
        AllocatorState allocator,
        ulong version,
        int runSequence,
        ArchetypeState[] archetypes,
        SparseStorage?[] sparse,
        SparseTagStorage?[] sparseTags,
        RelationStore?[] relations,
        RowStore?[] rows,
        IndexStore?[] indexes,
        HierarchyStore hierarchy,
        object?[] arenas,
        Entity[] singletonHolders,
        Entity singletonOwner,
        Entity[] pendingDestroys,
        WriteTag[]? destroyTags,
        Dictionary<SystemId, ulong>?[] queryRuns,
        int entityCount
    )
    {
        Owner = owner;
        Allocator = allocator;
        Version = version;
        RunSequence = runSequence;
        Archetypes = archetypes;
        Sparse = sparse;
        SparseTags = sparseTags;
        Relations = relations;
        Rows = rows;
        Indexes = indexes;
        Hierarchy = hierarchy;
        Arenas = arenas;
        SingletonHolders = singletonHolders;
        SingletonOwner = singletonOwner;
        PendingDestroys = pendingDestroys;
        DestroyTags = destroyTags;
        QueryRuns = queryRuns;
        EntityCount = entityCount;
    }

    internal World Owner { get; }
    internal AllocatorState Allocator { get; }
    internal ulong Version { get; }
    internal int RunSequence { get; }
    internal ArchetypeState[] Archetypes { get; }
    internal SparseStorage?[] Sparse { get; }
    internal SparseTagStorage?[] SparseTags { get; }
    internal RelationStore?[] Relations { get; }
    internal RowStore?[] Rows { get; }
    internal IndexStore?[] Indexes { get; }
    internal HierarchyStore Hierarchy { get; }
    internal object?[] Arenas { get; }
    internal Entity[] SingletonHolders { get; }
    internal Entity SingletonOwner { get; }
    internal Entity[] PendingDestroys { get; }
    internal WriteTag[]? DestroyTags { get; }
    internal Dictionary<SystemId, ulong>?[] QueryRuns { get; }

    /// <summary>Entities alive when the snapshot was taken, pending destroys included.</summary>
    public int EntityCount { get; }
}

/// <summary>Which operation a <see cref="RestoreInfo"/> reports.</summary>
public enum RestoreKind : byte
{
    /// <summary><see cref="World.Reset"/> rewound the whole world.</summary>
    Reset,

    /// <summary><see cref="World.Restore"/> added a captured subset.</summary>
    Subset,
}

/// <summary>What a restore did, handed to <see cref="IWorldAttachment.OnWorldRestored"/>.</summary>
public readonly ref struct RestoreInfo
{
    internal RestoreInfo(RestoreKind kind, LifecycleReason reason, ReadOnlySpan<Entity> created)
    {
        Kind = kind;
        Reason = reason;
        Created = created;
    }

    /// <summary>Whether the whole world was rewound or a subset added.</summary>
    public RestoreKind Kind { get; }

    /// <summary>The reason the restore logged; <see cref="LifecycleReason.Restored"/> for a reset or when none was given.</summary>
    public LifecycleReason Reason { get; }

    /// <summary>The entities a subset restore created, in image order. Empty for a reset.</summary>
    public ReadOnlySpan<Entity> Created { get; }
}

public sealed partial class World
{
    /// <summary>Takes a whole-world image for <see cref="Reset"/>. The world must be settled: no open iteration or
    /// <see cref="EntityBatch"/>, no unplayed commands and no observer events waiting for playback.</summary>
    [Effect(Effects.Settled)]
    public WorldSnapshot Snapshot()
    {
        CheckSettled(nameof(Snapshot));
        if (observers.HasPending)
            throw new InvalidOperationException(
                "Snapshot needs a settled world, and observer events are waiting for playback. Play back first."
            );
        var states = new ArchetypeState[archetypes.Count];
        for (var i = 0; i < states.Length; i++)
            states[i] = archetypes[i].Save();
        var runs = new Dictionary<SystemId, ulong>?[queries.Length];
        for (var i = 0; i < runs.Length; i++)
            runs[i] = queries[i] is { } query ? new(query.watermarks) : null;
        var arenaStates = new object?[arenas.Length];
        for (var i = 0; i < arenaStates.Length; i++)
            arenaStates[i] = (arenas[i] as IBufferArena)?.Save();
        return new WorldSnapshot(
            owner: this,
            allocator: allocator.Export(),
            version: version,
            runSequence: runSequence,
            archetypes: states,
            sparse: [.. sparse.Select(s => s?.Clone())],
            sparseTags: [.. sparseTags.Select(s => s?.Clone())],
            relations: [.. relations.Select(s => s?.Clone())],
            rows: [.. rowStores.Select(s => s?.Clone())],
            indexes: [.. indexStores.Select(s => s?.Clone())],
            hierarchy: hierarchy.Clone(),
            arenas: arenaStates,
            singletonHolders: [.. singletonHolders],
            singletonOwner: singletonOwner,
            pendingDestroys: [.. pendingDestroys],
            destroyTags: attributed?.DestroyTags(pendingDestroys),
            queryRuns: runs,
            entityCount: allocator.InUse
        );
    }

    /// <summary>Rewinds this world to <paramref name="baseline"/>: the same handles, values, stamps and change version, so the next tick
    /// runs as it did after the snapshot. Cost follows the stored data, not the work done since. A change cursor's next sample starts at
    /// version 0 and each <see cref="ChangeWatch{TBits}"/> marks everything at its next collect, observers are not called, and attachments
    /// get <see cref="IWorldAttachment.OnWorldRestored"/> in attach order. Commands not yet played back are dropped, so a reset also recovers a world left mid-tick by an exception.</summary>
    /// <exception cref="ArgumentException">The snapshot was taken from another world.</exception>
    /// <exception cref="InvalidOperationException">An iteration is still open.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public void Reset(WorldSnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (baseline.Owner != this)
            throw new ArgumentException(
                "The snapshot was taken from another world.",
                nameof(baseline)
            );
        CheckWritable();
        if (iterationDepth > 0)
            throw new InvalidOperationException(
                "Reset needs no open iteration; dispose the query enumerator first."
            );
        DiscardCommands();
        allocator.Import(baseline.Allocator);
        version = baseline.Version;
        runSequence = baseline.RunSequence;
        structuralCount++;
        DropArchetypesFrom(baseline.Archetypes.Length);
        for (var i = 0; i < archetypes.Count; i++)
            archetypes[i].Load(baseline.Archetypes[i]);
        for (var i = 0; i < sparse.Length; i++)
        {
            if (baseline.Sparse[i] is not { } saved)
                sparse[i] = null;
            else if (sparse[i] is { } live)
                live.Load(saved);
            else
                sparse[i] = saved.Clone();
        }
        for (var i = 0; i < sparseTags.Length; i++)
        {
            if (baseline.SparseTags[i] is not { } saved)
                sparseTags[i] = null;
            else if (sparseTags[i] is { } live)
                live.Load(saved);
            else
                sparseTags[i] = saved.Clone();
        }
        for (var i = 0; i < relations.Length; i++)
        {
            if (baseline.Relations[i] is not { } saved)
            {
                relations[i]?.Clear();
                relations[i] = null;
            }
            else if (relations[i] is { } live)
                live.Load(saved);
            else
                relations[i] = saved.Clone();
        }
        for (var i = 0; i < rowStores.Length; i++)
        {
            if (baseline.Rows[i] is not { } saved)
            {
                rowStores[i]?.Clear();
                rowStores[i] = null;
            }
            else if (rowStores[i] is { } live)
                live.Load(saved);
            else
                rowStores[i] = saved.Clone();
        }
        contested.Clear();
        for (var i = 0; i < indexStores.Length; i++)
        {
            if (baseline.Indexes[i] is not { } saved)
            {
                indexStores[i]?.Clear();
                indexStores[i] = null;
            }
            else if (indexStores[i] is { } live)
                live.Load(saved);
            else
                indexStores[i] = saved.Clone();
            if (indexStores[i] is { HasConflicts: true } store)
                contested.Add(store);
        }
        hierarchy.Load(baseline.Hierarchy);
        for (var i = 0; i < arenas.Length; i++)
            (arenas[i] as IBufferArena)?.Load(
                i < baseline.Arenas.Length ? baseline.Arenas[i] : null
            );
        baseline.SingletonHolders.CopyTo(singletonHolders, 0);
        singletonOwner = baseline.SingletonOwner;
        pendingDestroys.Clear();
        pendingDestroys.AddRange(baseline.PendingDestroys);
        observers.Discard();
        changes.Expire();
        attributed?.Clear();
        if (baseline.DestroyTags is { } tags && attributed is { } log)
        {
            for (var i = 0; i < tags.Length; i++)
                log.TagDestroy(baseline.PendingDestroys[i].Index, tags[i]);
        }
        foreach (var query in liveQueries)
        {
            var id = query.desc.Id;
            query.watermarks =
                id < baseline.QueryRuns.Length && baseline.QueryRuns[id] is { } runs
                    ? new(runs)
                    : [];
        }
        foreach (var attachment in attachmentOrder)
            attachment.OnWorldRestored(
                this,
                new RestoreInfo(RestoreKind.Reset, LifecycleReason.Restored, default)
            );
    }

    // Later archetypes must get the ids and query match order a fresh world gives them.
    void DropArchetypesFrom(int count)
    {
        for (var i = count; i < archetypes.Count; i++)
            bySignature.Remove(archetypes[i].Signature);
        archetypes.RemoveRange(count, archetypes.Count - count);
        foreach (var archetype in archetypes)
            archetype.ForgetFrom(count);
        foreach (var query in liveQueries)
            query.ForgetFrom(count);
        foreach (var bundle in bundles)
        {
            if (bundle?.Archetype is { } cached && cached.Id >= count)
                bundle.Archetype = null;
        }
    }

    void CheckSettled(string operation)
    {
        CheckWritable();
        if (iterationDepth > 0 || !stage.IsEmpty || submitted.Count > 0)
            throw new InvalidOperationException(
                $"{operation} needs a settled world: no open iteration and no unplayed commands. Play back first."
            );
        if (openBatches > 0)
            throw new InvalidOperationException(
                $"{operation} needs a settled world, and an entity batch is open. Dispose it first."
            );
    }
}
