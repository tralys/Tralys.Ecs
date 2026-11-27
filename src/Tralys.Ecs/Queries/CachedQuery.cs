using System.Numerics;
using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>A query bound to one world. Archetypes are matched as they appear, so a run only walks archetypes that match. Serial runs open a
/// walk in which structural changes record and apply when the outermost walk closes; partitioned runs record theirs through each chunk's
/// <see cref="ChunkView.Commands"/>.</summary>
public sealed class CachedQuery
{
    internal readonly World world;
    internal readonly QueryDesc desc;
    internal readonly QueryShape shape;
    readonly List<Matched> matched = [];
    ulong[] matchedBits = [];
    internal Dictionary<SystemId, ulong> watermarks = [];

    Entity[] snapshot = [];
    int snapshotCount;
    bool collecting;
    bool collectOpen;

    int[] workMatched = [];
    int[] workStart = [];
    int[] workCount = [];
    Entity[] workOwners = [];
    CommandBuffer[] workerBuffers = [];
    int rentedWorkers;
    object? partials;

    internal CachedQuery(World world, QueryDesc desc)
    {
        this.world = world;
        this.desc = desc;
        shape = world.registry.ShapeOf(desc);
        foreach (var archetype in world.archetypes)
            OnArchetype(archetype);
    }

    /// <summary>The world the query is bound to.</summary>
    public World World => world;

    /// <summary>The description the query matches.</summary>
    public QueryDesc Desc => desc;

    /// <summary>Archetypes the query matches now, empty ones included.</summary>
    public int MatchedArchetypeCount => matched.Count;

    internal int MatchedRowCount
    {
        get
        {
            var total = 0;
            foreach (var m in matched)
                total += m.Archetype.Count;
            return total;
        }
    }

    // Each calling system keeps its own Changed watermark, so two systems sharing a description do not consume each other's changes.
    internal ulong Since =>
        shape.HasChanged ? watermarks.GetValueOrDefault(world.currentSystem) : 0;

    /// <summary>The rows a run would visit now: matched, present, enabled, filtered and, for <c>Changed</c> terms, changed since the calling
    /// system's last completed run.</summary>
    public int Count()
    {
        var since = Since;
        var total = 0;
        foreach (var entry in matched)
        {
            var archetype = entry.Archetype;
            for (var at = 0; at < archetype.Count; at += archetype.ChunkCapacity)
                total += new RowSet(
                    RowMask(
                        entry,
                        at,
                        Math.Min(archetype.ChunkCapacity, archetype.Count - at),
                        aligned: true,
                        since
                    ),
                    new RowFilter(this, archetype, entry.Match, at)
                ).Count;
        }
        return total;
    }

    internal void OnArchetype(Archetype archetype)
    {
        if (shape.Match(archetype) is not { } match)
            return;
        var word = archetype.Id >> 6;
        if (word >= matchedBits.Length)
            Array.Resize(ref matchedBits, Math.Max(word + 1, matchedBits.Length * 2));
        matchedBits[word] |= 1UL << archetype.Id;
        matched.Add(new Matched(archetype, match));
    }

    internal void ForgetFrom(int count)
    {
        matched.RemoveAll(m => m.Archetype.Id >= count);
        for (var word = count >> 6; word < matchedBits.Length; word++)
            matchedBits[word] &= word == count >> 6 ? (1UL << count) - 1 : 0;
    }

    bool TryMatched(Archetype archetype, out Matched entry)
    {
        entry = default;
        var id = archetype.Id;
        var word = id >> 6;
        if (word >= matchedBits.Length || (matchedBits[word] & (1UL << id)) == 0)
            return false;
        int lo = 0,
            hi = matched.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var at = matched[mid].Archetype.Id;
            if (at == id)
            {
                entry = matched[mid];
                return true;
            }
            if (at < id)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return false;
    }

    internal ulong RowMask(in Matched entry, int start, int count, bool aligned, ulong since)
    {
        var archetype = entry.Archetype;
        var m = entry.Match;
        var mask = count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
        var word = start >> 6;
        var shift = start & 63;
        if (!desc.IncludePending)
            mask &= ~((archetype.DestroyWords[word] | archetype.AncestorWords[word]) >> shift);
        foreach (ref readonly var source in m.Required.AsSpan())
            mask &= Bits(archetype, source, word) >> shift;
        foreach (ref readonly var source in m.Excluded.AsSpan())
            mask &= ~(Bits(archetype, source, word) >> shift);
        foreach (var group in m.WordGroups)
        {
            var any = 0UL;
            foreach (ref readonly var source in group.AsSpan())
                any |= Bits(archetype, source, word) >> shift;
            mask &= any;
        }
        if (mask == 0)
            return 0;
        if (m.ChangedTracked.Length > 0)
        {
            if (aligned)
            {
                var chunk = start / archetype.ChunkCapacity;
                foreach (var t in m.ChangedTracked)
                {
                    if (archetype.MaxChangedIn(chunk, t) <= since)
                        return 0;
                }
            }
            for (var bits = mask; bits != 0; bits &= bits - 1)
            {
                var i = BitOperations.TrailingZeroCount(bits);
                foreach (var t in m.ChangedTracked)
                {
                    if (archetype.ChangedAt(t, start + i) <= since)
                    {
                        mask &= ~(1UL << i);
                        break;
                    }
                }
            }
        }
        if (shape.RowProbe && mask != 0)
        {
            var entities = archetype.Entities;
            for (var bits = mask; bits != 0; bits &= bits - 1)
            {
                var i = BitOperations.TrailingZeroCount(bits);
                if (!RowPasses(entry, start + i, entities[start + i].Index, since))
                    mask &= ~(1UL << i);
            }
        }
        return mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Bits(Archetype archetype, in WordSource source, int word)
    {
        if (source.Always)
            return ulong.MaxValue;
        var words = archetype.Words;
        var bits = source.Enable >= 0 ? archetype.EnableWords[(source.Enable * words) + word] : 0;
        if (source.Inherited > 0)
            bits |= archetype.AncestorWords[(source.Inherited * words) + word];
        return bits;
    }

    bool RowPasses(in Matched entry, int row, uint index, ulong since)
    {
        foreach (var t in shape.SparseRequired)
        {
            if (!SparseHas(t, index))
                return false;
        }
        foreach (var t in shape.SparseExcluded)
        {
            if (SparseHas(t, index))
                return false;
        }
        foreach (var t in shape.SparseChanged)
        {
            if (
                world.sparse[world.layout.SparseSlot[shape.Types[t]]] is not { } storage
                || storage.SlotOf(index) is not (var at and >= 0)
                || storage.Changed[at] <= since
            )
                return false;
        }
        var m = entry.Match;
        foreach (var group in shape.Groups)
        {
            if (!group.HasSparse)
                continue;
            var any = false;
            foreach (var t in group.Members)
            {
                any = shape.Classes[t] is StorageClass.Sparse or StorageClass.SparseTag
                    ? SparseHas(t, index)
                    : (
                        m.Present[t]
                        && (m.Enable[t] < 0 || entry.Archetype.IsEnabled(m.Enable[t], row))
                    )
                        || (
                            shape.Inherited[t] > 0
                            && entry.Archetype.HasAncestor(shape.Inherited[t], row)
                        );
                if (any)
                    break;
            }
            if (!any)
                return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal bool Skips(Archetype archetype, ArchetypeMatch match, int row) =>
        archetype.IsPending(row)
            ? !StillMatches(archetype, match, row)
            : world.overlay.HasLifecycle && RecordedAway(archetype.Entities[row]);

    bool RecordedAway(Entity entity) =>
        world.AwayAsRecorded(entity, dying: !desc.IncludePending, detached: !desc.IncludeDetached);

    bool StillMatches(Archetype archetype, ArchetypeMatch match, int row)
    {
        var overlay = world.overlay;
        var index = archetype.Entities[row].Index;
        if (overlay.HasLifecycle && RecordedAway(archetype.Entities[row]))
            return false;
        var terms = desc.Terms;
        for (var t = 0; t < terms.Length; t++)
        {
            var kind = terms[t].Kind;
            if (
                kind is TermKind.Optional or TermKind.AnyOf
                || !overlay.TryGet(index, shape.Types[t], out _, out var enabled, out _)
            )
                continue;
            var holds = enabled || Inherits(archetype, t, row);
            if (kind == TermKind.Without ? holds : !holds)
                return false;
        }
        foreach (var group in shape.Groups)
        {
            var any = false;
            foreach (var t in group.Members)
            {
                any =
                    overlay.TryGet(index, shape.Types[t], out _, out var enabled, out _)
                        ? enabled || Inherits(archetype, t, row)
                    : shape.Classes[t] is StorageClass.Sparse or StorageClass.SparseTag
                        ? SparseHas(t, index)
                    : (
                        match.Present[t]
                        && (match.Enable[t] < 0 || archetype.IsEnabled(match.Enable[t], row))
                    ) || Inherits(archetype, t, row);
                if (any)
                    break;
            }
            if (!any)
                return false;
        }
        return true;
    }

    bool Inherits(Archetype archetype, int term, int row) =>
        shape.Inherited[term] > 0 && archetype.HasAncestor(shape.Inherited[term], row);

    bool SparseHas(int term, uint index)
    {
        var type = shape.Types[term];
        return shape.Classes[term] == StorageClass.Sparse
            ? world.sparse[world.layout.SparseSlot[type]]?.Contains(index) == true
            : world.sparseTags[world.layout.SparseTagSlot[type]]?.Contains(index) == true;
    }

    bool TryDriver(out SparseStorage? driver)
    {
        driver = null;
        if (shape.Drivers == 0)
            return false;
        foreach (var t in shape.SparseRequired)
        {
            if (shape.Classes[t] != StorageClass.Sparse)
                continue;
            var storage = world.sparse[world.layout.SparseSlot[shape.Types[t]]];
            if (storage is null)
            {
                driver = null;
                return true;
            }
            if (driver is null || storage.Count < driver.Count)
                driver = storage;
        }
        return driver is not null && driver.Count < MatchedRowCount;
    }

    internal void AddToSnapshot(Entity entity)
    {
        if (!collecting)
            throw new InvalidOperationException("Collect is only valid inside a collect run.");
        if (snapshotCount == snapshot.Length)
            Array.Resize(ref snapshot, Math.Max(16, snapshotCount * 2));
        snapshot[snapshotCount++] = entity;
    }

    /// <summary>Runs <paramref name="kernel"/> over every matched chunk on the calling thread, in archetype then row order.</summary>
    public void Run<TKernel>(ref TKernel kernel)
        where TKernel : IChunkKernel, allows ref struct
    {
        Walk(ref kernel);
        world.ThrowIndexConflicts();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Walk<TKernel>(ref TKernel kernel)
        where TKernel : IChunkKernel, allows ref struct
    {
        world.CheckWritable();
        var since = Since;
        var runVersion = world.version;
        var completed = false;
        var cursor = new ChunkCursor(this, since);
        try
        {
            while (cursor.MoveNext(out var view))
            {
                kernel.Execute(ref view);
                if (view.stopped)
                    break;
            }
            completed = !cursor.Pending;
        }
        catch (Exception error)
        {
            cursor.Fault(error);
            throw;
        }
        finally
        {
            Finish(completed, runVersion);
            cursor.Dispose();
        }
    }

    /// <summary>Runs <paramref name="kernel"/> over every matched row in order of hierarchy depth, so parents run before their children;
    /// rows at one depth keep chunk order.</summary>
    public void RunDepthOrdered<TKernel>(ref TKernel kernel)
        where TKernel : IChunkKernel, allows ref struct
    {
        WalkDepthOrdered(ref kernel);
        world.ThrowIndexConflicts();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void WalkDepthOrdered<TKernel>(ref TKernel kernel)
        where TKernel : IChunkKernel, allows ref struct
    {
        world.CheckWritable();
        var since = Since;
        var runVersion = world.version;
        var completed = false;
        var capacity = MatchedRowCount;
        var entities = world.Scratch.Rent<Entity>(capacity);
        var keys = world.Scratch.Rent<long>(capacity);
        try
        {
            var count = 0;
            var cursor = new ChunkCursor(this, since);
            try
            {
                while (cursor.MoveNext(out var view))
                {
                    var rows = view.Entities;
                    for (var bits = view.RowMask; bits != 0; bits &= bits - 1)
                    {
                        var entity = rows[BitOperations.TrailingZeroCount(bits)];
                        entities.Span[count] = entity;
                        keys.Span[count] = ((long)world.DepthOf(entity) << 32) | (uint)count;
                        count++;
                    }
                }
            }
            finally
            {
                cursor.Dispose();
            }
            keys.Span[..count].Sort(entities.Span[..count]);
            var structural = world.structuralCount;
            var iteration = world.BeginIteration();
            try
            {
                for (var i = 0; i < count; i++)
                {
                    if (!SingleView(entities.Span[i], i, since, out var view, count: false))
                        continue;
                    kernel.Execute(ref view);
                    CheckStructural(structural);
                    if (view.stopped)
                        return;
                }
                completed = true;
            }
            catch (Exception error)
            {
                iteration.Fault(error);
                throw;
            }
            finally
            {
                iteration.Dispose();
            }
        }
        finally
        {
            entities.Dispose();
            keys.Dispose();
            Finish(completed, runVersion);
        }
    }

    /// <summary>Runs <paramref name="kernel"/> for each listed entity the query matches, in list order. Changed terms use the query's last
    /// completed run, which this run does not advance.</summary>
    public void RunFor<TKernel>(ref TKernel kernel, ReadOnlySpan<Entity> entities)
        where TKernel : IChunkKernel, allows ref struct
    {
        WalkFor(ref kernel, entities);
        world.ThrowIndexConflicts();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void WalkFor<TKernel>(ref TKernel kernel, ReadOnlySpan<Entity> entities)
        where TKernel : IChunkKernel, allows ref struct
    {
        world.CheckWritable();
        var structural = world.structuralCount;
        var iteration = world.BeginIteration();
        try
        {
            for (var i = 0; i < entities.Length; i++)
            {
                if (!SingleView(entities[i], i, Since, out var view))
                    continue;
                kernel.Execute(ref view);
                CheckStructural(structural);
                if (view.stopped)
                    return;
            }
        }
        catch (Exception error)
        {
            iteration.Fault(error);
            throw;
        }
        finally
        {
            iteration.Dispose();
        }
    }

    /// <summary>Folds every matched chunk on the calling thread, in archetype then row order, and returns the combined partials.</summary>
    public TAccumulator Reduce<TAccumulator, TKernel>(ref TKernel kernel)
        where TAccumulator : struct, IReduction<TAccumulator>
        where TKernel : IReducingKernel<TAccumulator>, allows ref struct
    {
        var result = Fold<TAccumulator, TKernel>(ref kernel);
        world.ThrowIndexConflicts();
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    TAccumulator Fold<TAccumulator, TKernel>(ref TKernel kernel)
        where TAccumulator : struct, IReduction<TAccumulator>
        where TKernel : IReducingKernel<TAccumulator>, allows ref struct
    {
        world.CheckWritable();
        var since = Since;
        var runVersion = world.version;
        var completed = false;
        var result = TAccumulator.Identity;
        var cursor = new ChunkCursor(this, since);
        try
        {
            while (cursor.MoveNext(out var view))
            {
                var partial = TAccumulator.Identity;
                kernel.Execute(ref view, ref partial);
                result = TAccumulator.Combine(result, partial);
                if (view.stopped)
                    break;
            }
            completed = !cursor.Pending;
        }
        catch (Exception error)
        {
            cursor.Fault(error);
            throw;
        }
        finally
        {
            Finish(completed, runVersion);
            cursor.Dispose();
        }
        return result;
    }

    /// <summary>Folds each listed entity the query matches, in list order. Changed terms use the query's last completed run, which this run does not advance.</summary>
    public TAccumulator ReduceFor<TAccumulator, TKernel>(
        ref TKernel kernel,
        ReadOnlySpan<Entity> entities
    )
        where TAccumulator : struct, IReduction<TAccumulator>
        where TKernel : IReducingKernel<TAccumulator>, allows ref struct
    {
        var result = FoldFor<TAccumulator, TKernel>(ref kernel, entities);
        world.ThrowIndexConflicts();
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    TAccumulator FoldFor<TAccumulator, TKernel>(ref TKernel kernel, ReadOnlySpan<Entity> entities)
        where TAccumulator : struct, IReduction<TAccumulator>
        where TKernel : IReducingKernel<TAccumulator>, allows ref struct
    {
        world.CheckWritable();
        var structural = world.structuralCount;
        var result = TAccumulator.Identity;
        var iteration = world.BeginIteration();
        try
        {
            for (var i = 0; i < entities.Length; i++)
            {
                if (!SingleView(entities[i], i, Since, out var view))
                    continue;
                var partial = TAccumulator.Identity;
                kernel.Execute(ref view, ref partial);
                CheckStructural(structural);
                result = TAccumulator.Combine(result, partial);
                if (view.stopped)
                    break;
            }
        }
        catch (Exception error)
        {
            iteration.Fault(error);
            throw;
        }
        finally
        {
            iteration.Dispose();
        }
        return result;
    }

    /// <summary>Collects the entities <paramref name="kernel"/> marks in a serial walk, closes the walk, applying what it recorded, then
    /// acts on each one still eligible, with immediate structural changes allowed. With <see cref="World.Faults"/> set, an act that throws
    /// is reported and the rest still run.</summary>
    public void Collect<TKernel, TAction>(ref TKernel kernel, ref TAction action)
        where TKernel : IChunkKernel, allows ref struct
        where TAction : IEntityAction, allows ref struct
    {
        world.CheckWritable();
        if (collectOpen)
            throw new InvalidOperationException("A collect run of this query is already open.");
        collectOpen = collecting = true;
        try
        {
            Run(ref kernel);
            collecting = false;
            var faults = world.Faults;
            for (var i = 0; i < snapshotCount; i++)
            {
                var entity = snapshot[i];
                if (!Admits(entity))
                    continue;
                if (faults is null)
                {
                    action.Act(entity);
                    continue;
                }
                try
                {
                    action.Act(entity);
                }
                catch (Exception error)
                {
                    faults.OnEntityFault(world.CurrentSystem, entity, error);
                }
            }
        }
        finally
        {
            Array.Clear(snapshot, 0, snapshotCount);
            snapshotCount = 0;
            collectOpen = collecting = false;
        }
    }

    bool Admits(Entity entity) =>
        world.StateOf(entity) switch
        {
            LifecycleState.Alive => true,
            LifecycleState.PendingDestroy => desc.IncludePending,
            _ => false,
        } && (desc.IncludeDetached || !world.IsDetached(entity));

    /// <summary>Runs <paramref name="kernel"/> over every matched chunk through the world's executor. The body may not change the world
    /// directly: structural changes go through <see cref="ChunkView.Commands"/>, value writes through the chunk's columns. When a chunk
    /// throws, every command and index edit the run recorded is dropped; column writes already made stay.</summary>
    public void RunParallel<TKernel>(ref TKernel kernel)
        where TKernel : struct, IChunkKernel
    {
        world.CheckWritable();
        var since = Since;
        var runVersion = world.version;
        var items = BuildWork();
        var executor = world.executor;
        var job = new PartitionJob<TKernel>
        {
            Query = this,
            Kernel = kernel,
            Buffers = RentWorkers(executor.WorkerCount),
            Since = since,
        };
        var completed = false;
        IndexConflictException? conflict;
        world.parallelDepth++;
        world.iterationDepth++;
        try
        {
            executor.Run(ref job, items);
            completed = true;
        }
        finally
        {
            conflict = EndPartitioned(completed);
            Finish(completed, runVersion);
        }
        if (conflict is not null)
            throw conflict;
    }

    /// <summary>Folds matched chunks through the world's executor and combines the partials in chunk order, so the result does not depend on the workers. The body may not change the world directly.</summary>
    public TAccumulator ReduceParallel<TAccumulator, TKernel>(ref TKernel kernel)
        where TAccumulator : struct, IReduction<TAccumulator>
        where TKernel : struct, IReducingKernel<TAccumulator>
    {
        world.CheckWritable();
        var since = Since;
        var runVersion = world.version;
        var items = BuildWork();
        var executor = world.executor;
        if (partials is not TAccumulator[] slots || slots.Length < items)
            partials = slots = new TAccumulator[Math.Max(items, 16)];
        slots.AsSpan(0, items).Fill(TAccumulator.Identity);
        var job = new ReduceJob<TKernel, TAccumulator>
        {
            Query = this,
            Kernel = kernel,
            Buffers = RentWorkers(executor.WorkerCount),
            Since = since,
            Partials = slots,
        };
        var completed = false;
        IndexConflictException? conflict;
        world.parallelDepth++;
        world.iterationDepth++;
        try
        {
            executor.Run(ref job, items);
            completed = true;
        }
        finally
        {
            conflict = EndPartitioned(completed);
            Finish(completed, runVersion);
        }
        if (conflict is not null)
            throw conflict;
        var result = TAccumulator.Identity;
        for (var i = 0; i < items; i++)
            result = TAccumulator.Combine(result, slots[i]);
        return result;
    }

    CommandBuffer[] RentWorkers(int workers)
    {
        if (workerBuffers.Length < workers)
            workerBuffers = new CommandBuffer[workers];
        for (rentedWorkers = 0; rentedWorkers < workers; rentedWorkers++)
            workerBuffers[rentedWorkers] = world.RentBuffer(world.runSequence, 0);
        return workerBuffers;
    }

    IndexConflictException? EndPartitioned(bool completed)
    {
        world.parallelDepth--;
        if (world.iterationDepth > 0)
            world.iterationDepth--;
        var rented = workerBuffers.AsSpan(0, rentedWorkers);
        var conflict = completed ? world.ApplyIndexEdits(rented) : null;
        foreach (var buffer in rented)
        {
            world.chunksVisited += buffer.chunksVisited;
            world.chunksSkipped += buffer.chunksSkipped;
            buffer.chunksVisited = buffer.chunksSkipped = 0;
            if (completed)
                world.Submit(buffer);
            else
                world.ReturnBuffer(buffer);
        }
        rented.Clear();
        rentedWorkers = 0;
        // Serial commands recorded later in the same system run play back after this run's chunk buffers; a walk's apply before them, as it closes.
        world.runSequence++;
        return conflict;
    }

    int BuildWork()
    {
        var count = 0;
        if (TryDriver(out var driver))
        {
            if (driver is null)
                return 0;
            for (var i = 0; i < driver.Count; i++)
            {
                ref var slot = ref world.allocator.Slots[driver.Owners[i].Index];
                if (slot.Archetype is not { } archetype || !TryMatched(archetype, out _))
                    continue;
                Reserve(count + 1);
                workOwners[count] = driver.Owners[i];
                workMatched[count] = -1;
                count++;
            }
            return count;
        }
        for (var m = 0; m < matched.Count; m++)
        {
            var archetype = matched[m].Archetype;
            var capacity = archetype.ChunkCapacity;
            for (var start = 0; start < archetype.Count; start += capacity)
            {
                Reserve(count + 1);
                workMatched[count] = m;
                workStart[count] = start;
                workCount[count] = Math.Min(capacity, archetype.Count - start);
                count++;
            }
        }
        return count;
    }

    void Reserve(int count)
    {
        if (workMatched.Length >= count)
            return;
        var size = Math.Max(count, Math.Max(16, workMatched.Length * 2));
        Array.Resize(ref workMatched, size);
        Array.Resize(ref workStart, size);
        Array.Resize(ref workCount, size);
        if (workOwners.Length < size)
            Array.Resize(ref workOwners, size);
    }

    internal bool WorkView(
        int item,
        int worker,
        CommandBuffer buffer,
        ulong since,
        out ChunkView view
    )
    {
        buffer.chunkIndex = item;
        Matched m;
        int start,
            count;
        bool aligned;
        if (workMatched[item] < 0)
        {
            ref var slot = ref world.allocator.Slots[workOwners[item].Index];
            TryMatched(slot.Placed, out m);
            start = slot.Row;
            count = 1;
            aligned = false;
        }
        else
        {
            m = matched[workMatched[item]];
            start = workStart[item];
            count = workCount[item];
            aligned = true;
        }
        var rows = RowMask(m, start, count, aligned, since);
        if (rows == 0)
            buffer.chunksSkipped++;
        else
            buffer.chunksVisited++;
        view = new ChunkView(this, m, start, count, rows, item, worker, buffer);
        return rows != 0;
    }

    bool SingleView(Entity entity, int index, ulong since, out ChunkView view, bool count = true)
    {
        view = default;
        ref var slot = ref world.allocator.Current(entity);
        if (
            Unsafe.IsNullRef(ref slot)
            || slot.Archetype is not { } archetype
            || !TryMatched(archetype, out var m)
        )
            return false;
        var rows = RowMask(m, slot.Row, 1, aligned: false, since);
        if (count ? !world.CountChunk(rows) : rows == 0)
            return false;
        view = new ChunkView(this, m, slot.Row, 1, rows, index, 0, null);
        return true;
    }

    void Finish(bool completed, ulong runVersion)
    {
        if (!shape.HasChanged)
            return;
        if (completed)
            watermarks[world.currentSystem] = runVersion;
        // A write landing after this run must carry a newer version than the one this run consumed.
        world.version++;
    }

    void CheckStructural(int structural)
    {
        if (world.structuralCount != structural)
            throw new InvalidOperationException(
                "Rows moved while a query was iterating; a playback inside a query body is not allowed."
            );
    }

    /// <summary>Walks matched chunks serially, for hand-written loops, in which structural changes record. Disposing the enumerator closes
    /// the walk, which applies them if it is the outermost.</summary>
    public ChunkEnumerator GetEnumerator() => new(this);

    /// <summary>Walks the matched chunks of a serial run; disposing it closes the iteration.</summary>
    public ref struct ChunkEnumerator
    {
        readonly CachedQuery query;
        readonly ulong runVersion;
        ChunkCursor cursor;
        ChunkView current;
        bool completed;

        internal ChunkEnumerator(CachedQuery query)
        {
            query.world.CheckWritable();
            this.query = query;
            runVersion = query.world.version;
            cursor = new ChunkCursor(query, query.Since);
        }

        /// <summary>The chunk at the current position.</summary>
        public readonly ChunkView Current => current;

        /// <summary>Moves to the next matched chunk; false past the last, which completes the run.</summary>
        public bool MoveNext()
        {
            if (cursor.MoveNext(out current))
                return true;
            completed = true;
            return false;
        }

        /// <summary>Closes the iteration, advancing <c>Changed</c> watermarks only if the walk completed, applies what the outermost walk
        /// recorded, and throws <see cref="IndexConflictException"/> for a unique-key conflict it left, or what applying the records threw, as
        /// <see cref="World.Playback"/> does. A <c>foreach</c> cannot tell it the body threw, so that throw replaces the body's exception;
        /// <see cref="Run{TKernel}"/> throws both.</summary>
        public void Dispose()
        {
            query.Finish(completed, runVersion);
            cursor.Dispose();
            query.world.ThrowIndexConflicts();
        }
    }

    internal ref struct ChunkCursor
    {
        readonly CachedQuery query;
        readonly ulong since;
        readonly int structural;
        World.IterationScope iteration;
        ScratchBuffer<Entity> owners;
        readonly bool driven;
        int m;
        int start;
        int chunk;
        int owner;

        public ChunkCursor(CachedQuery query, ulong since)
        {
            this.query = query;
            this.since = since;
            var world = query.world;
            structural = world.structuralCount;
            iteration = world.BeginIteration();
            if (query.TryDriver(out var driver))
            {
                driven = true;
                var count = driver?.Count ?? 0;
                owners = world.Scratch.Rent<Entity>(count);
                driver?.Owners.AsSpan(0, count).CopyTo(owners.Span);
            }
            m = 0;
            start = 0;
            chunk = 0;
            owner = 0;
        }

        // A stopped walk still has chunks pending, so it does not count as completed.
        public readonly bool Pending =>
            driven ? owner < owners.Span.Length : m < query.matched.Count;

        public bool MoveNext(out ChunkView view)
        {
            query.CheckStructural(structural);
            if (driven)
                return NextOwner(out view);
            var matched = query.matched;
            while (m < matched.Count)
            {
                var entry = matched[m];
                var archetype = entry.Archetype;
                if (start >= archetype.Count)
                {
                    m++;
                    start = 0;
                    continue;
                }
                var count = Math.Min(archetype.ChunkCapacity, archetype.Count - start);
                var at = start;
                var index = chunk++;
                start += archetype.ChunkCapacity;
                var rows = query.RowMask(entry, at, count, aligned: true, since);
                if (!query.world.CountChunk(rows))
                    continue;
                view = new ChunkView(query, entry, at, count, rows, index, 0, null);
                return true;
            }
            view = default;
            return false;
        }

        bool NextOwner(out ChunkView view)
        {
            var world = query.world;
            var list = owners.Span;
            while (owner < list.Length)
            {
                var index = owner++;
                ref var slot = ref world.allocator.Current(list[index]);
                if (
                    Unsafe.IsNullRef(ref slot)
                    || slot.Archetype is not { } archetype
                    || !query.TryMatched(archetype, out var entry)
                )
                    continue;
                var rows = query.RowMask(entry, slot.Row, 1, aligned: false, since);
                if (!world.CountChunk(rows))
                    continue;
                view = new ChunkView(query, entry, slot.Row, 1, rows, index, 0, null);
                return true;
            }
            view = default;
            return false;
        }

        public void Fault(Exception error) => iteration.Fault(error);

        public void Dispose()
        {
            owners.Dispose();
            iteration.Dispose();
        }
    }
}

internal struct PartitionJob<TKernel> : IPartitionJob
    where TKernel : struct, IChunkKernel
{
    public CachedQuery Query;
    public TKernel Kernel;
    public CommandBuffer[] Buffers;
    public ulong Since;

    public readonly void Execute(int item, int worker)
    {
        if (!Query.WorkView(item, worker, Buffers[worker], Since, out var view))
            return;
        var kernel = Kernel;
        kernel.Execute(ref view);
    }
}

internal struct ReduceJob<TKernel, TAccumulator> : IPartitionJob
    where TKernel : struct, IReducingKernel<TAccumulator>
    where TAccumulator : struct, IReduction<TAccumulator>
{
    public CachedQuery Query;
    public TKernel Kernel;
    public CommandBuffer[] Buffers;
    public ulong Since;
    public TAccumulator[] Partials;

    public readonly void Execute(int item, int worker)
    {
        if (!Query.WorkView(item, worker, Buffers[worker], Since, out var view))
            return;
        var kernel = Kernel;
        kernel.Execute(ref view, ref Partials[item]);
    }
}
