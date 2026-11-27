using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Tralys.Ecs;

/// <summary>What one playback did; read the last one from <see cref="WorldIntrospection.LastPlayback"/>.</summary>
public readonly record struct PlaybackStats(
    int Applied,
    int Folded,
    int StaleSkipped,
    int ObserverRounds,
    int Finalized
);

/// <summary>Thrown at the end of a playback that dropped buffered commands which failed validation, such as an addition that broke a
/// requirement or singleton claim; the rest of the playback completed. <see cref="Rejections"/> names each one.</summary>
public sealed class CommandRejectedException : InvalidOperationException
{
    internal CommandRejectedException(IReadOnlyList<string> rejections)
        : base(
            "Playback dropped buffered commands that failed validation:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, rejections)
        ) => Rejections = rejections;

    /// <summary>One message per dropped command, naming the command and why it failed.</summary>
    public IReadOnlyList<string> Rejections { get; }
}

public sealed partial class World
{
    const int MaxPlaybackRounds = 64;

    readonly List<CommandBuffer> buffersById = [];
    readonly List<CommandBuffer> submitted = [];
    readonly Stack<CommandBuffer> bufferPool = new();
    CommandRecord[] merged = new CommandRecord[64];
    CommandRecord[] batchRecords = new CommandRecord[16];
    int openBatches;

    // A reset or clear abandons the open batches, so one disposed after it closes nothing.
    int batchEpoch;
    RecordBlock[] blocks = new RecordBlock[16];
    readonly Dictionary<long, Entity> placeholders = [];
    uint rentals;
    int[] foldOf = [];
    EntityFold[] folds = new EntityFold[32];
    int foldCount;
    TypeState[] states = new TypeState[64];
    int stateCount;
    Emission[] emissions = new Emission[64];
    int emissionCount;
    ulong[]? targetWords;
    int[] applyOrder = new int[32];
    long[] applyKeys = new long[32];
    readonly List<int> sideRecords = [];
    int[] emissionRecords = [];
    int[] destroyRecords = [];
    Entity[] batchClaims = [];
    readonly List<string> rejections = [];
    readonly List<Exception> commandFailures = [];
    readonly List<Exception> observerFailures = [];
    bool playing;
    internal PlaybackStats lastPlayback;

    // A destroy recorded in the batch being applied is judged once the batch has landed, and may yet be spared.
    readonly List<Entity> destroyedRoots = [];
    readonly HashSet<Entity> provisional = [];

    struct Superseded
    {
        public int Fold;
        public int Type;
        public int Buffer;
        public int Payload;
    }

    Superseded[] superseded = new Superseded[16];
    int supersededCount;

    struct RecordBlock
    {
        public CommandRecord[] Records;
        public int Start;
        public int Count;
    }

    struct EntityFold
    {
        public Entity Entity;
        public bool Created;
        public bool Cancelled;

        // Only an entity the batch created destroys in the fold; every other destroy and cancel replays in record order.
        public bool Destroy;
        public LifecycleReason DestroyReason;
        public sbyte Partition;
        public int PartitionEmission;

        // The last record that moved the entity into or out of an index lane: its create or partition change.
        public int LaneRecord;
        public int FirstState;
        public int Source;
        public int Target;
    }

    struct TypeState
    {
        public int Type;
        public bool Before;
        public bool Present;
        public bool HasValue;
        public int Buffer;
        public int Payload;
        public bool EnableBefore;
        public bool Enable;
        public bool Tracked;

        // A tracked record brought it into presence, so its stamp counts as an add; a later tracked write alone stamps a change.
        public bool AddTracked;
        public bool RemoveTracked;
        public bool Unchecked;

        // Present through a Set after being absent at some point in the batch; with Before, the component was re-added.
        public bool Added;
        public int Emission;
        public int Old;
        public bool Replaced;
        public int ReplacedOld;
        public int ReplacedNew;

        // The records the log credits: the one that ended the presence the batch started with, and the last that changed anything.
        public int RemovedBy;
        public int ChangedBy;
        public int Sequence;
        public int Next;
    }

    enum EmissionKind : byte
    {
        Created,
        Component,
        Detached,
        Reattached,
    }

    struct Emission
    {
        public EmissionKind Kind;
        public int Fold;
        public int Type;
        public int State;
        public LifecycleReason Reason;
        public bool Dead;
    }

    internal CommandBuffer RentBuffer(int schedulePosition, int chunkIndex)
    {
        var buffer =
            bufferPool.Count > 0 ? bufferPool.Pop() : new CommandBuffer(this, parallel: true);
        if (buffer.id == 0)
        {
            buffer.id = buffersById.Count;
            buffersById.Add(buffer);
        }
        buffer.schedulePosition = schedulePosition;
        buffer.chunkIndex = chunkIndex;
        buffer.rental = ++rentals == 0 ? ++rentals : rentals;
        return buffer;
    }

    internal void Submit(CommandBuffer buffer)
    {
        if (buffer.IsEmpty)
            ReturnBuffer(buffer);
        else
            submitted.Add(buffer);
    }

    /// <summary>Whether a non-finalising <see cref="Playback"/> has work: recorded commands to apply or observer events to deliver.</summary>
    public bool PlaybackOwed => !stage.IsEmpty || submitted.Count > 0 || observers.HasPending;

    void DiscardCommands()
    {
        openBatches = 0;
        batchEpoch++;
        stage.Clear();
        ForgetWaiting();
        foreach (var buffer in submitted)
        {
            buffer.Clear();
            bufferPool.Push(buffer);
        }
        submitted.Clear();
        overlay.Clear();
    }

    internal void ReturnBuffer(CommandBuffer buffer)
    {
        buffer.Clear();
        bufferPool.Push(buffer);
    }

    /// <summary>Applies every deferred command and delivers queued observer events, then, unless <paramref name="finalizeDestroys"/> is
    /// false, finalises pending destroys; observer work feeds back into the same playback until quiet. An observer that throws stops
    /// nothing: the playback finishes, then throws.</summary>
    /// <exception cref="IndexConflictException">A unique index declared with <see cref="ConflictPolicy.Throw"/> saw a conflict; thrown
    /// once the playback has finished, so the world is consistent.</exception>
    /// <exception cref="CommandRejectedException">Buffered commands failed validation; they were dropped and the rest of the playback
    /// completed.</exception>
    /// <exception cref="TargetPolicyException">A recorded destroy, or a pending one being finalised, would release the target of a
    /// <see cref="TargetPolicy.Throw"/> relation whose source outlives it, or a recorded write would leave such a source pointing at a dying
    /// target; that destroy was spared, or that write dropped, and the rest of the playback completed.</exception>
    /// <exception cref="InvalidOperationException">A buffered command failed when applied, such as a parenting that would make a cycle; or
    /// a query is iterating, an observer called <see cref="Playback"/>, or an <see cref="EntityBatch"/> is open. Nothing was played back in
    /// the last three cases.</exception>
    /// <exception cref="AggregateException">More than one of the failures above, or of the observers' exceptions.</exception>
    [Effect(
        Effects.Structural
            | Effects.MovesEntities
            | Effects.Mutates
            | Effects.OwnerThread
            | Effects.MovesRowBlocks
    )]
    public void Playback(bool finalizeDestroys = true)
    {
        CheckWritable();
        if (playing)
            throw new InvalidOperationException(
                "Playback cannot run inside another playback, such as from an observer."
            );
        if (openBatches > 0)
            throw new InvalidOperationException(
                "Playback cannot run while an EntityBatch is open; dispose the batch first."
            );
        if (iterationDepth > 0)
            throw new InvalidOperationException(
                "Playback cannot run inside a query body; call it after the run."
            );
        PlaybackStats stats = default;
        Exception? failure = null;
        IndexConflictException? conflict;
        playing = true;
        BeginIndexBatch();
        try
        {
            stats = Play(finalizeDestroys);
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            playing = false;
            attributed?.EndReplay();
            conflict = EndIndexBatch();
        }
        var dangling = failure is null && verifyReferences ? VerifyAfterPlayback() : null;
        var broken = failure is null && verifyInvariants ? BrokenInvariant() : null;
        lastPlayback = stats;
        ThrowCollected(null, failure, conflict, dangling, broken);
    }

    void ThrowCollected(
        string? closing,
        Exception? failure,
        IndexConflictException? conflict,
        Exception? dangling,
        Exception? broken
    )
    {
        if (
            failure is null
            && conflict is null
            && rejections.Count == 0
            && commandFailures.Count == 0
            && observerFailures.Count == 0
            && dangling is null
            && broken is null
        )
            return;
        var errors = new List<Exception>(4);
        if (failure is not null)
            errors.Add(failure);
        errors.AddRange(commandFailures);
        commandFailures.Clear();
        errors.AddRange(observerFailures);
        observerFailures.Clear();
        if (conflict is not null)
            errors.Add(conflict);
        if (rejections.Count > 0)
        {
            errors.Add(new CommandRejectedException([.. rejections]));
            rejections.Clear();
        }
        if (dangling is not null)
            errors.Add(dangling);
        if (broken is not null)
            errors.Add(broken);
        if (errors.Count > 1)
            throw new AggregateException(
                closing is null
                    ? "Playback failed in more than one way."
                    : $"Closing the {closing} failed in more than one way.",
                errors
            );
        ExceptionDispatchInfo.Throw(errors[0]);
    }

    PlaybackStats Play(bool finalize)
    {
        version++;
        rejections.Clear();
        commandFailures.Clear();
        observerFailures.Clear();
        int applied = 0,
            folded = 0,
            stale = 0,
            rounds = 0,
            finalized = 0;
        while (true)
        {
            if (rounds == MaxPlaybackRounds)
                throw new InvalidOperationException(
                    $"Observers kept queueing work for {MaxPlaybackRounds} playback rounds."
                );
            var progress = ApplyCommands(ref applied, ref folded, ref stale);
            int delivered;
            using (BeginDelivery())
                delivered = observers.Deliver(observerFailures);
            if (delivered > 0)
                rounds++;
            var finalisedAny = false;
            if (finalize && !progress && delivered == 0 && stage.IsEmpty)
            {
                var count = FinalizeDestroys();
                finalisedAny = count > 0;
                finalized += count;
            }
            if (
                !progress
                && delivered == 0
                && !finalisedAny
                && stage.IsEmpty
                && !observers.HasPending
            )
                break;
        }
        observers.ResetStores();
        return new PlaybackStats(applied, folded, stale, rounds, finalized);
    }

    bool ApplyCommands(ref int applied, ref int folded, ref int stale)
    {
        var total = stage.count;
        foreach (var buffer in submitted)
            total += buffer.count;
        if (total == 0)
            return false;
        var records = Ordered(total);
        try
        {
            ResolvePlaceholders(records);
            ApplyRecords(records, ref stale, ref folded);
        }
        finally
        {
            stage.Clear();
            ForgetWaiting();
            overlay.Clear();
            foreach (var buffer in submitted)
            {
                buffer.Clear();
                bufferPool.Push(buffer);
            }
            submitted.Clear();
        }
        applied += total;
        return true;
    }

    void ApplyRecords(Span<CommandRecord> records, ref int stale, ref int folded)
    {
        // A serial record keeps the position of its call; a parallel command takes effect now, in playback order.
        var now = indexClock;
        indexClock += records.Length;
        for (var r = 0; r < records.Length; r++)
        {
            if (buffersById[records[r].Buffer].parallel)
                records[r].Position = now + r + 1;
        }
        try
        {
            FoldAndApply(records, ref stale, ref folded);
            ApplySide(records, ref stale);
            GuardRecordedDestroys();
        }
        catch
        {
            AbandonBatch(records);
            throw;
        }
        finally
        {
            indexAt = 0;
            sideRecords.Clear();
            destroyedRoots.Clear();
            provisional.Clear();
            attributed?.EndReplay();
        }
    }

    static long IndexPositionOf(Span<CommandRecord> records, int record) =>
        record >= 0 ? records[record].Position : 0;

    void FoldAndApply(Span<CommandRecord> records, ref int stale, ref int folded)
    {
        partitionCascades.Clear();
        Fold(records, ref stale, ref folded);
        Validate();
        Apply(records);
        FreeSuperseded();
        Emit(records);
        CascadePartitions(records);
    }

    readonly List<int> partitionCascades = [];

    // A moved entity's descendants follow it after the batch's own entries, as a cascade's consequences.
    void CascadePartitions(Span<CommandRecord> records)
    {
        foreach (var e in partitionCascades)
        {
            ref readonly var emission = ref emissions[e];
            indexAt = IndexPositionOf(records, folds[emission.Fold].LaneRecord);
            foreach (var child in hierarchy.ChildrenOf(folds[emission.Fold].Entity))
                RefreshPartitions(child, emission.Reason);
        }
        partitionCascades.Clear();
    }

    /// <summary>Opens a batch of changes to <paramref name="entity"/> that land in one archetype move when it is disposed. Inside a query
    /// walk they wait until the walk closes; inside an observer, or while the entity already has changes waiting for playback, they wait
    /// for the next playback instead.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist.</exception>
    [Effect(Effects.Structural | Effects.Mutates)]
    public EntityBatch Batch(Entity entity)
    {
        CheckWritable();
        Structural();
        ref var slot = ref Slot(entity);
        if (RecordsStructure(slot))
            return new EntityBatch(this, entity, -1);
        MarkPending(ref slot);
        openBatches++;
        return new EntityBatch(this, entity, batchEpoch);
    }

    internal void CloseBatch(Entity entity, int epoch)
    {
        if (epoch != batchEpoch)
            return;
        openBatches--;
        ref var slot = ref allocator.Current(entity);
        if (Unsafe.IsNullRef(ref slot) || !slot.Pending || iterationDepth > 0 || playing)
            return;
        CheckWritable();
        var sides = false;
        for (var i = 0; i < stage.count; i++)
            sides |= stage.records[i].Entity != entity && Side(stage.records[i].Kind);
        var count = 0;
        for (var i = 0; i < stage.count; i++)
        {
            if (stage.records[i].Entity != entity)
                continue;
            // The full playback applies these in record order with every entity's reparents and edges, which a destroy is judged by.
            if (
                Side(stage.records[i].Kind)
                || (sides && stage.records[i].Kind == CommandKind.Destroy)
            )
                return;
            if (count == batchRecords.Length)
                Array.Resize(ref batchRecords, count * 2);
            batchRecords[count++] = stage.records[i];
        }
        ApplyOutsidePlayback(batchRecords.AsSpan(0, count), entity, "batch");

        static bool Side(CommandKind kind) =>
            kind is >= CommandKind.Relate or CommandKind.CancelDestroy;
    }

    // What the walk recorded applies as a playback applies it; its events wait for the next playback, and nothing is finalised.
    void ApplyWalk(Exception? fault)
    {
        if (stage.IsEmpty)
            return;
        version++;
        try
        {
            ApplyOutsidePlayback(stage.records.AsSpan(0, stage.count), default, "walk");
        }
        catch (Exception applying) when (fault is not null)
        {
            throw new AggregateException(
                "The query body threw, and applying what it had recorded failed too.",
                fault,
                applying
            );
        }
    }

    void ApplyOutsidePlayback(Span<CommandRecord> records, Entity batch, string closing)
    {
        rejections.Clear();
        commandFailures.Clear();
        IndexConflictException? conflict;
        BeginIndexBatch();
        try
        {
            int stale = 0,
                folded = 0;
            ApplyRecords(records, ref stale, ref folded);
        }
        finally
        {
            if (batch.IsDefault)
            {
                stage.Clear();
                ForgetWaiting();
                overlay.Clear();
            }
            else
            {
                ClearPending(ref allocator.Slots[batch.Index]);
                stage.Drop(batch);
                overlay.Drop(batch.Index);
            }
            conflict = EndIndexBatch();
        }
        VerifyInvariants();
        ThrowCollected(closing, null, conflict, null, null);
    }

    // Records within one buffer are already in sequence order, so ordering whole runs orders every record.
    Span<CommandRecord> Ordered(int total)
    {
        var blockCount = 0;
        AddBlocks(stage, ref blockCount);
        foreach (var buffer in submitted)
            AddBlocks(buffer, ref blockCount);
        if (blockCount == 1)
            return blocks[0].Records.AsSpan(blocks[0].Start, blocks[0].Count);
        blocks
            .AsSpan(0, blockCount)
            .Sort(static (a, b) => PlaybackOrder(a.Records[a.Start], b.Records[b.Start]));
        if (merged.Length < total)
            merged = new CommandRecord[Math.Max(total, merged.Length * 2)];
        var at = 0;
        foreach (ref readonly var block in blocks.AsSpan(0, blockCount))
        {
            Array.Copy(block.Records, block.Start, merged, at, block.Count);
            at += block.Count;
        }
        return merged.AsSpan(0, total);
    }

    static int PlaybackOrder(in CommandRecord x, in CommandRecord y)
    {
        var c = x.SchedulePosition.CompareTo(y.SchedulePosition);
        if (c != 0)
            return c;
        c = x.ChunkIndex.CompareTo(y.ChunkIndex);
        if (c != 0)
            return c;
        c = x.Buffer.CompareTo(y.Buffer);
        return c != 0 ? c : x.Sequence.CompareTo(y.Sequence);
    }

    void AddBlocks(CommandBuffer buffer, ref int blockCount)
    {
        var records = buffer.records;
        var start = 0;
        for (var i = 1; i <= buffer.count; i++)
        {
            if (
                i < buffer.count
                && records[i].SchedulePosition == records[start].SchedulePosition
                && records[i].ChunkIndex == records[start].ChunkIndex
            )
                continue;
            if (blockCount == blocks.Length)
                Array.Resize(ref blocks, blockCount * 2);
            blocks[blockCount++] = new RecordBlock
            {
                Records = records,
                Start = start,
                Count = i - start,
            };
            start = i;
        }
    }

    void ResolvePlaceholders(Span<CommandRecord> records)
    {
        placeholders.Clear();
        foreach (ref var record in records)
        {
            if (record.Kind == CommandKind.Create && record.Entity.IsPlaceholder)
            {
                var created = allocator.Allocate(LifecycleState.Reserved);
                placeholders[Key(record.Entity)] = created;
                record.Entity = created;
            }
        }
        if (placeholders.Count == 0)
            return;
        foreach (ref var record in records)
        {
            for (var i = 0; i < record.Operands; i++)
            {
                ref var operand = ref record.Operand(i);
                if (operand.IsPlaceholder && placeholders.TryGetValue(Key(operand), out var real))
                    operand = real;
            }
            if (!CarriesValue(record.Kind) || record.Payload < 0)
                continue;
            var offsets = registry[record.Type].EntityRefOffsets;
            if (offsets.IsEmpty)
                continue;
            ref var value = ref buffersById[record.Buffer]
                .Payload(record.Type)
                .RowRef(record.Payload);
            foreach (var offset in offsets)
            {
                ref var reference = ref Unsafe.As<byte, Entity>(ref Unsafe.Add(ref value, offset));
                if (
                    reference.IsPlaceholder
                    && reference.PlaceholderRental == buffersById[record.Buffer].rental
                    && placeholders.TryGetValue(Key(reference), out var target)
                )
                    reference = target;
            }
        }

        static long Key(Entity placeholder) =>
            ((long)placeholder.PlaceholderRental << 32) | (uint)placeholder.PlaceholderLocal;
    }

    // A RemoveRow payload is only the key, so it names entities but owns no buffers.
    static bool CarriesValue(CommandKind kind) =>
        kind
            is CommandKind.Set
                or CommandKind.Relate
                or CommandKind.UpsertRow
                or CommandKind.RemoveRow;

    static bool OwnsValue(CommandKind kind) =>
        kind is CommandKind.Set or CommandKind.Relate or CommandKind.UpsertRow;

    void Fold(Span<CommandRecord> records, ref int stale, ref int folded)
    {
        for (var f = 0; f < foldCount; f++)
            foldOf[folds[f].Entity.Index] = 0;
        if (foldOf.Length < allocator.Capacity)
            Array.Resize(ref foldOf, Math.Max(allocator.Capacity, foldOf.Length * 2));
        foldCount = 0;
        stateCount = 0;
        emissionCount = 0;
        supersededCount = 0;
        for (var r = 0; r < records.Length; r++)
        {
            ref readonly var record = ref records[r];
            if (record.Kind >= CommandKind.Relate)
            {
                sideRecords.Add(r);
                continue;
            }
            if (!allocator.IsCurrent(record.Entity))
            {
                stale++;
                DropRecordedValue(in record);
                continue;
            }
            if (
                record.Kind is CommandKind.Destroy or CommandKind.CancelDestroy
                && !CreatedInBatch(record.Entity)
            )
            {
                sideRecords.Add(r);
                continue;
            }
            var f = FoldFor(record.Entity);
            ref var fold = ref folds[f];
            switch (record.Kind)
            {
                case CommandKind.Create:
                    fold.Created = true;
                    fold.LaneRecord = r;
                    AddEmission(EmissionKind.Created, f, -1, default, r);
                    if (partitionsUsed)
                        fold.PartitionEmission = AddEmission(
                            EmissionKind.Detached,
                            f,
                            -1,
                            default,
                            r
                        );
                    break;
                case CommandKind.Destroy:
                    if (!fold.Cancelled)
                        fold.DestroyReason = record.Reason;
                    if (attributed is not null)
                        TrackDestroy(f, r);
                    fold.Cancelled = true;
                    break;
                case CommandKind.CancelDestroy:
                    fold.Cancelled = false;
                    fold.DestroyReason = default;
                    break;
                case CommandKind.Detach:
                case CommandKind.Reattach:
                {
                    var detach = record.Kind == CommandKind.Detach;
                    fold.Partition = (sbyte)(detach ? 1 : 0);
                    fold.LaneRecord = r;
                    if (fold.PartitionEmission >= 0)
                        emissions[fold.PartitionEmission].Dead = true;
                    fold.PartitionEmission = AddEmission(
                        detach ? EmissionKind.Detached : EmissionKind.Reattached,
                        f,
                        -1,
                        record.Reason,
                        r
                    );
                    break;
                }
                case CommandKind.Set:
                {
                    ref var state = ref State(f, record.Type, r);
                    var changes =
                        !state.Present || !state.Enable || Differs(fold.Entity, state, record);
                    var tracked = !record.Untracked;
                    if (!state.Present)
                    {
                        state.Present = true;
                        state.Added = true;
                        state.Unchecked = buffersById[record.Buffer].parallel;
                        state.AddTracked = tracked;
                    }
                    else if (!state.Enable)
                        state.AddTracked |= tracked;
                    state.Enable = true;
                    if (changes)
                        state.ChangedBy = r;
                    if (state.HasValue && layout.OwnsBuffers[record.Type])
                        Supersede(f, state);
                    state.HasValue = record.Payload >= 0;
                    state.Buffer = record.Buffer;
                    state.Payload = record.Payload;
                    state.Sequence = record.Sequence;
                    // A tracked write of the value already there stamps nothing, as the immediate one does.
                    state.Tracked |= (changes && tracked) || record.TrackedEdit;
                    break;
                }
                case CommandKind.Remove:
                {
                    ref var state = ref State(f, record.Type, r);
                    if (!state.Present)
                        break;
                    if (state.Added)
                    {
                        DropPayload(state);
                        folded++;
                    }
                    else
                    {
                        var staged = state;
                        state.HasValue = false;
                        if (layout.OwnsBuffers[record.Type])
                            Vacating(record.Type, ref FoldedValue(fold.Entity, state));
                        DropPayload(staged);
                    }
                    if (state.Before && state.RemovedBy < 0)
                        state.RemovedBy = r;
                    state.RemoveTracked |= !record.Untracked;
                    state.ChangedBy = r;
                    state.Present = false;
                    state.HasValue = false;
                    state.Added = false;
                    break;
                }
                case CommandKind.Enable:
                case CommandKind.Disable:
                {
                    ref var state = ref State(f, record.Type, r);
                    var enable = record.Kind == CommandKind.Enable;
                    if (!state.Present)
                    {
                        if (enable)
                            rejections.Add(NoneToEnable(record.Type, record.Entity));
                        break;
                    }
                    if (state.Enable == enable)
                        break;
                    state.Tracked |= !record.Untracked;
                    if (enable)
                        state.AddTracked |= !record.Untracked;
                    state.Enable = enable;
                    state.ChangedBy = r;
                    break;
                }
            }
        }
        for (var f = 0; f < foldCount; f++)
        {
            ref var fold = ref folds[f];
            if (fold.Cancelled && !CreateCancellable(fold.Entity, records))
            {
                fold.Cancelled = false;
                fold.Destroy = true;
            }
            if (fold.Cancelled)
                folded++;
        }
    }

    bool CreatedInBatch(Entity entity) =>
        foldOf[entity.Index] is var mark and > 0 && folds[mark - 1].Created;

    int FoldFor(Entity entity)
    {
        ref var mark = ref foldOf[entity.Index];
        if (mark > 0)
            return mark - 1;
        if (foldCount == folds.Length)
            Array.Resize(ref folds, foldCount * 2);
        ref var slot = ref allocator.Slots[entity.Index];
        folds[foldCount] = new EntityFold
        {
            Entity = entity,
            Partition = -1,
            PartitionEmission = -1,
            LaneRecord = -1,
            FirstState = -1,
            Source = slot.Archetype?.Id ?? -1,
            Target = -1,
        };
        mark = foldCount + 1;
        return foldCount++;
    }

    ref TypeState State(int f, int type, int record)
    {
        ref var fold = ref folds[f];
        for (var s = fold.FirstState; s >= 0; s = states[s].Next)
        {
            if (states[s].Type == type)
                return ref states[s];
        }
        if (stateCount == states.Length)
            Array.Resize(ref states, stateCount * 2);
        ref var slot = ref allocator.Slots[fold.Entity.Index];
        bool before,
            enableBefore;
        if (slot.Archetype is not { } archetype)
            before = enableBefore = false;
        else if (layout.Class[type] is StorageClass.Column or StorageClass.SignatureTag)
        {
            var at = archetype.Map.IndexOf(type);
            before = at >= 0;
            enableBefore =
                before
                && (
                    archetype.Map.SlotAt(at).Enable is not (var enable and >= 0)
                    || archetype.IsEnabled(enable, slot.Row)
                );
        }
        else
            before = enableBefore = StoredIn(slot, fold.Entity, type);
        states[stateCount] = new TypeState
        {
            Type = type,
            Before = before,
            Present = before,
            Buffer = -1,
            Payload = -1,
            EnableBefore = enableBefore,
            Enable = enableBefore,
            Emission = AddEmission(EmissionKind.Component, f, type, default, record),
            Old = -1,
            ReplacedOld = -1,
            ReplacedNew = -1,
            RemovedBy = -1,
            ChangedBy = -1,
            Next = fold.FirstState,
        };
        ref var created = ref states[stateCount];
        emissions[created.Emission].State = stateCount;
        if (before && enableBefore && observers.For(type) is { WantsRemove: true })
        {
            ref var stored = ref FoldedValue(fold.Entity, created);
            created.Old = observers.CaptureDeparting(
                type,
                ref Unsafe.IsNullRef(ref stored) ? ref ZeroBytes.Value : ref stored
            );
        }
        fold.FirstState = stateCount;
        return ref states[stateCount++];
    }

    unsafe bool Differs(Entity entity, in TypeState state, in CommandRecord record)
    {
        if (record.Payload < 0)
            return false;
        ref var current = ref FoldedValue(entity, state);
        return Unsafe.IsNullRef(ref current)
            || !registry[record.Type]
                .Ops.ValuesEqual(
                    ref current,
                    ref buffersById[record.Buffer].Payload(record.Type).RowRef(record.Payload)
                );
    }

    ref byte FoldedValue(Entity entity, in TypeState state) =>
        ref state.HasValue
            ? ref buffersById[state.Buffer].Payload(state.Type).RowRef(state.Payload)
            : ref StoredValue(entity, state.Type);

    ref byte StoredValue(Entity entity, int type)
    {
        ref var slot = ref allocator.Slots[entity.Index];
        switch (layout.Class[type])
        {
            case StorageClass.Column:
            {
                var archetype = slot.Placed;
                var column = archetype.ColumnSlot(type);
                return ref column >= 0
                    ? ref archetype.RowRef(column, slot.Row)
                    : ref Unsafe.NullRef<byte>();
            }
            case StorageClass.Sparse:
            {
                var storage = StoredSparse(type);
                return ref storage.RowRef(storage.SlotOf(entity.Index));
            }
        }
        return ref Unsafe.NullRef<byte>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int AddEmission(EmissionKind kind, int fold, int type, LifecycleReason reason, int record)
    {
        if (emissionCount == emissions.Length)
            Array.Resize(ref emissions, emissionCount * 2);
        emissions[emissionCount] = new Emission
        {
            Kind = kind,
            Fold = fold,
            Type = type,
            Reason = reason,
        };
        if (attributed is not null)
            TrackEmission(record);
        return emissionCount++;
    }

    void Validate()
    {
        var pending = false;
        for (var s = 0; s < stateCount && !pending; s++)
            pending = IsUnchecked(states[s]);
        if (!pending)
            return;
        RejectUnmetRequirements();
        if (layout.SingletonCount > 0 && RejectRivalClaims())
            RejectUnmetRequirements();
    }

    // Requirements settle first: a claim by an add that is then rejected would turn away a rival that keeps its add.
    bool RejectRivalClaims()
    {
        if (batchClaims.Length < layout.SingletonCount)
            batchClaims = new Entity[layout.SingletonCount];
        else
            Array.Clear(batchClaims);
        var rejected = false;
        for (var f = 0; f < foldCount; f++)
        {
            if (folds[f].Cancelled)
                continue;
            var entity = folds[f].Entity;
            for (var s = folds[f].FirstState; s >= 0; s = states[s].Next)
            {
                ref var state = ref states[s];
                var singleton = layout.SingletonSlot[state.Type];
                if (singleton < 0 || !IsUnchecked(state))
                    continue;
                var holder = SingletonRival(entity, singleton);
                if (!holder.IsDefault && ReleasedInBatch(holder, state.Type))
                    holder = default;
                if (holder.IsDefault)
                    holder = batchClaims[singleton];
                if (holder.IsDefault || holder == entity)
                    batchClaims[singleton] = entity;
                else
                {
                    Reject(ref state, SingletonConflict(state.Type, holder, entity.ToString()));
                    rejected = true;
                }
            }
        }
        return rejected;
    }

    void RejectUnmetRequirements()
    {
        bool rejected;
        do
        {
            rejected = false;
            for (var f = 0; f < foldCount; f++)
            {
                if (folds[f].Cancelled)
                    continue;
                for (var s = folds[f].FirstState; s >= 0; s = states[s].Next)
                {
                    ref var state = ref states[s];
                    if (!IsUnchecked(state))
                        continue;
                    var descriptor = registry[state.Type];
                    foreach (var required in descriptor.Requires)
                    {
                        if (FoldHas(f, required))
                            continue;
                        Reject(
                            ref state,
                            MissingRequirement(descriptor, required, folds[f].Entity)
                        );
                        rejected = true;
                        break;
                    }
                }
            }
        } while (rejected);
    }

    static bool IsUnchecked(in TypeState state) => state.Unchecked && state.Present && state.Added;

    void Reject(ref TypeState state, string reason)
    {
        DropPayload(state);
        state.Added = false;
        state.Present = false;
        state.HasValue = false;
        state.Unchecked = false;
        rejections.Add(reason);
    }

    void Supersede(int fold, in TypeState state)
    {
        if (supersededCount == superseded.Length)
            Array.Resize(ref superseded, supersededCount * 2);
        superseded[supersededCount++] = new Superseded
        {
            Fold = fold,
            Type = state.Type,
            Buffer = state.Buffer,
            Payload = state.Payload,
        };
    }

    // Not at fold time: the final value may hold a superseded handle again.
    void FreeSuperseded()
    {
        for (var i = 0; i < supersededCount; i++)
        {
            ref readonly var old = ref superseded[i];
            ref var outgoing = ref buffersById[old.Buffer].Payload(old.Type).RowRef(old.Payload);
            ref readonly var final = ref State(old.Fold, old.Type, -1);
            if (final.Present && final.HasValue)
                FreeReplacedBuffers(
                    old.Type,
                    ref outgoing,
                    ref buffersById[final.Buffer].Payload(old.Type).RowRef(final.Payload)
                );
            else
                FreeBuffers(old.Type, ref outgoing);
        }
        supersededCount = 0;
    }

    void DropPayload(in TypeState state)
    {
        if (state.HasValue && layout.OwnsBuffers[state.Type])
            FreeBuffers(
                state.Type,
                ref buffersById[state.Buffer].Payload(state.Type).RowRef(state.Payload)
            );
    }

    bool FoldHas(int f, int type)
    {
        ref readonly var fold = ref folds[f];
        for (var s = fold.FirstState; s >= 0; s = states[s].Next)
        {
            if (states[s].Type == type)
                return states[s].Present;
        }
        return StoredIn(allocator.Slots[fold.Entity.Index], fold.Entity, type);
    }

    bool ReleasedInBatch(Entity holder, int type)
    {
        var mark = foldOf[holder.Index];
        if (mark == 0 || folds[mark - 1].Entity != holder)
            return false;
        ref readonly var fold = ref folds[mark - 1];
        if (fold.Cancelled)
            return true;
        for (var s = fold.FirstState; s >= 0; s = states[s].Next)
        {
            if (states[s].Type == type)
                return !states[s].Present;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void TrackEmission(int record)
    {
        if (emissionRecords.Length <= emissionCount)
            Array.Resize(ref emissionRecords, Math.Max(32, emissionCount * 2));
        emissionRecords[emissionCount] = record;
    }

    // A cancelled create skips finalisation and the policies it applies, so any side record naming the entity keeps the create.
    bool CreateCancellable(Entity entity, Span<CommandRecord> records)
    {
        foreach (var r in sideRecords)
        {
            ref var record = ref records[r];
            for (var i = 0; i < record.Operands; i++)
            {
                if (record.Operand(i) == entity)
                    return false;
            }
        }
        return true;
    }

    void AbandonBatch(Span<CommandRecord> records)
    {
        foreach (ref readonly var record in records)
        {
            if (record.Entity.IsPlaceholder || !allocator.IsCurrent(record.Entity))
                continue;
            ref var slot = ref allocator.Slots[record.Entity.Index];
            ClearPending(ref slot);
            if (slot.State == LifecycleState.Reserved)
                Release(record.Entity, default, LogKind.Destroyed);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void TrackDestroy(int fold, int record)
    {
        if (destroyRecords.Length <= fold)
            Array.Resize(ref destroyRecords, Math.Max(32, fold * 2));
        destroyRecords[fold] = record;
    }

    void Apply(Span<CommandRecord> records)
    {
        var log = attributed;
        if (applyOrder.Length < foldCount)
        {
            applyOrder = new int[Math.Max(foldCount, applyOrder.Length * 2)];
            applyKeys = new long[applyOrder.Length];
        }
        for (var f = 0; f < foldCount; f++)
        {
            ref var fold = ref folds[f];
            if (!fold.Cancelled)
                fold.Target = TargetOf(ref fold).Id;
            applyOrder[f] = f;
            applyKeys[f] = ((long)fold.Source << 32) | (uint)fold.Target;
        }
        applyKeys.AsSpan(0, foldCount).Sort(applyOrder.AsSpan(0, foldCount));

        for (var i = 0; i < foldCount; i++)
        {
            var f = applyOrder[i];
            ref var fold = ref folds[f];
            var entity = fold.Entity;
            ref var slot = ref allocator.Slots[entity.Index];
            if (fold.Cancelled)
            {
                for (var s = fold.FirstState; s >= 0; s = states[s].Next)
                {
                    if (states[s].Present)
                        DropPayload(states[s]);
                }
                if (log is not null)
                    log.BeginReplay(TagOf(records[destroyRecords[f]]));
                Release(entity, fold.DestroyReason, LogKind.Destroyed);
                log?.EndReplay();
                continue;
            }
            var target = archetypes[fold.Target];
            var wasDetached = slot.Archetype is { Detached: true };
            if (fold.Created)
            {
                slot.State = LifecycleState.Alive;
                Place(entity, ref slot, target);
            }
            else if (fold.Source != fold.Target)
                MoveTo(entity, ref slot, target);
            if (fold.Partition >= 0)
                slot.DetachedSelf = fold.Partition == 1;
            if (fold.PartitionEmission >= 0)
            {
                var moved = slot.Detached != wasDetached;
                ref var emission = ref emissions[fold.PartitionEmission];
                emission.Dead = !moved;
                emission.Kind = slot.Detached ? EmissionKind.Detached : EmissionKind.Reattached;
                if (moved)
                    hierarchy.RaiseAncestors(entity, version);
                if (moved && hierarchy.HasChildren(entity))
                    partitionCascades.Add(fold.PartitionEmission);
            }

            for (var s = fold.FirstState; s >= 0; s = states[s].Next)
            {
                ref var state = ref states[s];
                ApplyState(
                    entity,
                    ref slot,
                    ref state,
                    ref state.HasValue
                        ? ref buffersById[state.Buffer].Payload(state.Type).RowRef(state.Payload)
                        : ref Unsafe.NullRef<byte>()
                );
            }

            if (fold.Destroy)
            {
                if (log is not null)
                    log.BeginReplay(TagOf(records[destroyRecords[f]]));
                MarkDestroyed(entity, ref slot, fold.DestroyReason, recorded: true);
                log?.EndReplay();
            }
            ClearPending(ref slot);
        }
        if (!hasIndexes)
            return;
        // Folds are in record order and the loop above is not; the order picks which entity a shared unique key goes to.
        for (var f = 0; f < foldCount; f++)
        {
            if (!folds[f].Cancelled)
                RefreshFoldIndexes(records, in folds[f]);
        }
    }

    // Runs once the slot is no longer pending, so the index reads stored values rather than the overlay.
    void RefreshFoldIndexes(Span<CommandRecord> records, in EntityFold fold)
    {
        var moved =
            fold.Created
            || fold.Destroy
            || (fold.PartitionEmission >= 0 && !emissions[fold.PartitionEmission].Dead);
        var lane = moved ? fold.LaneRecord : -1;
        for (var s = fold.FirstState; s >= 0; s = states[s].Next)
        {
            indexAt = Math.Max(
                IndexPositionOf(records, states[s].ChangedBy),
                IndexPositionOf(records, lane)
            );
            RefreshIndexes(fold.Entity, states[s].Type);
        }
        if (moved || fold.Partition >= 0)
        {
            indexAt = IndexPositionOf(records, lane);
            RefreshAllIndexes(fold.Entity);
        }
    }

    Archetype TargetOf(ref EntityFold fold)
    {
        var source = fold.Source >= 0 ? archetypes[fold.Source] : root;
        var detached =
            fold.Partition >= 0 || fold.Created ? FinalDetached(fold.Entity) : source.Detached;
        var moved = -1;
        var moves = 0;
        for (var s = fold.FirstState; s >= 0; s = states[s].Next)
        {
            if (MovesArchetype(in states[s]))
            {
                moved = s;
                moves++;
            }
        }
        if (moves == 0)
            return Partitioned(source, detached);
        if (moves == 1 && detached == source.Detached)
            return states[moved].Present
                ? WithType(source, states[moved].Type)
                : WithoutType(source, states[moved].Type);

        var words = targetWords ??= new ulong[root.Signature.Words.Length];
        source.Signature.Words.CopyTo(words, 0);
        for (var s = fold.FirstState; s >= 0; s = states[s].Next)
        {
            ref readonly var state = ref states[s];
            if (!MovesArchetype(in state))
                continue;
            if (state.Present)
                words[state.Type >> 6] |= 1UL << state.Type;
            else
                words[state.Type >> 6] &= ~(1UL << state.Type);
        }
        return ArchetypeFor(words, detached);
    }

    // From the batch's final flags, so a fold lands where it ends up whichever of its ancestors' folds applies first.
    bool FinalDetached(Entity entity)
    {
        for (var node = entity; !node.IsDefault; node = hierarchy.ParentOf(node))
        {
            var mark = foldOf[node.Index];
            var self =
                mark > 0 && folds[mark - 1].Entity == node && folds[mark - 1].Partition >= 0
                    ? folds[mark - 1].Partition == 1
                    : allocator.Slots[node.Index].DetachedSelf;
            if (self)
                return true;
        }
        return false;
    }

    bool MovesArchetype(in TypeState state) =>
        state.Present != state.Before
        && layout.Class[state.Type] is StorageClass.Column or StorageClass.SignatureTag;

    // Both routes apply a component's net change here; the caller moves the archetype and reports.
    ref byte ApplyState(Entity entity, ref EntitySlot slot, ref TypeState state, ref byte incoming)
    {
        var type = state.Type;
        if (!state.Present)
        {
            ReleaseSingleton(entity, type);
            if (state.Before && state.RemoveTracked)
                PropagateRemoval(entity, type);
            switch (layout.Class[type])
            {
                case StorageClass.Sparse:
                    if (SparseFor(type).Remove(entity.Index))
                        slot.SparseCount--;
                    break;
                case StorageClass.SparseTag:
                    SetSparseTag(entity, ref slot, type, false);
                    break;
            }
            return ref Unsafe.NullRef<byte>();
        }
        if (state.Added)
            HoldSingleton(entity, type);
        switch (layout.Class[type])
        {
            case StorageClass.Sparse when !state.Before:
            {
                if (Unsafe.IsNullRef(ref incoming))
                    return ref Unsafe.NullRef<byte>();
                var storage = SparseFor(type);
                var at = storage.AddFrom(entity, ref incoming);
                slot.SparseCount++;
                if (state.Tracked && storage.Stamped)
                    Stamp(StampsAdded(in state), storage, at);
                return ref storage.RowRef(at);
            }
            case StorageClass.SparseTag:
                SetSparseTag(entity, ref slot, type, true);
                return ref Unsafe.NullRef<byte>();
        }
        return ref ApplyValue(entity, ref slot, ref state, ref incoming);
    }

    void SetSparseTag(Entity entity, ref EntitySlot slot, int type, bool present)
    {
        if (
            (sparseTags[layout.SparseTagSlot[type]] ??= new SparseTagStorage(type)).Set(
                entity.Index,
                present
            )
        )
            slot.SparseCount = (ushort)(slot.SparseCount + (present ? 1 : -1));
    }

    // A stored component's value write and enable flip, which move nothing.
    ref byte ApplyValue(Entity entity, ref EntitySlot slot, ref TypeState state, ref byte incoming)
    {
        var type = state.Type;
        if (layout.Class[type] == StorageClass.Sparse)
        {
            var storage = StoredSparse(type);
            var at = storage.SlotOf(entity.Index);
            ref var current = ref storage.RowRef(at);
            if (!Unsafe.IsNullRef(ref incoming) && Overwrites(ref state, ref current, ref incoming))
            {
                storage.WriteFrom(at, ref incoming);
                if (state.Tracked && storage.Stamped)
                    Stamp(StampsAdded(in state), storage, at);
            }
            return ref current;
        }
        var archetype = slot.Placed;
        var row = slot.Row;
        ref readonly var typeSlot = ref archetype.Map.SlotAt(archetype.Map.IndexOf(type));
        ref var cell = ref typeSlot.Column >= 0
            ? ref archetype.RowRef(typeSlot.Column, row)
            : ref Unsafe.NullRef<byte>();
        var written = state.Added;
        if (
            !Unsafe.IsNullRef(ref cell)
            && !Unsafe.IsNullRef(ref incoming)
            && Overwrites(ref state, ref cell, ref incoming)
        )
        {
            registry[type].Column.Store(archetype.Columns[typeSlot.Column], row, ref incoming);
            written = true;
        }
        var enable = typeSlot.Enable;
        var flips = enable >= 0 && archetype.IsEnabled(enable, row) != state.Enable;
        if (flips)
            archetype.SetEnabled(enable, row, state.Enable);
        if (state.Tracked && typeSlot.Tracked >= 0 && (written || flips))
            Stamp(StampsAdded(in state), archetype, typeSlot.Tracked, row);
        if (flips && layout.InheritedSlot[type] > 0 && hierarchy.HasChildren(entity))
            RefreshChildren(entity);
        return ref cell;
    }

    // Equal values write nothing; an overwrite captures a replace-only change and frees the buffers it drops before the write.
    unsafe bool Overwrites(ref TypeState state, ref byte cell, ref byte incoming)
    {
        if (state.Added)
            return true;
        var type = state.Type;
        if (registry[type].Ops.ValuesEqual(ref cell, ref incoming))
            return false;
        if (layout.ReplaceOnly[type])
            Replaced(ref state, ref cell, ref incoming);
        if (layout.OwnsBuffers[type])
            FreeReplacedBuffers(type, ref cell, ref incoming);
        return true;
    }

    static TypeState Adding(int type, bool tracked) =>
        new()
        {
            Type = type,
            Present = true,
            Enable = true,
            Added = true,
            Tracked = tracked,
            AddTracked = tracked,
            ReplacedOld = -1,
            ReplacedNew = -1,
        };

    static TypeState Kept(int type, bool enabledBefore, bool enabled, bool tracked) =>
        new()
        {
            Type = type,
            Before = true,
            EnableBefore = enabledBefore,
            Present = true,
            Enable = enabled,
            Tracked = tracked,
            AddTracked = tracked,
            ReplacedOld = -1,
            ReplacedNew = -1,
        };

    static TypeState Removing(int type, bool enabledBefore, bool tracked) =>
        new()
        {
            Type = type,
            Before = true,
            EnableBefore = enabledBefore,
            RemoveTracked = tracked,
            ReplacedOld = -1,
            ReplacedNew = -1,
        };

    void Report(in TypeState state, Entity entity, ref byte old, ref byte value)
    {
        var change = ChangeOf(in state);
        Log(change, entity, state.Type, default);
        if (change.HearRemove)
            observers.QueueRemove(entity, state.Type, ref Bytes(ref old), default);
        if (change.HearAdd)
            observers.QueueAdd(entity, state.Type, ref Bytes(ref value), default);
        if (change.HearReplace)
            observers.QueueCapturedReplace(
                entity,
                state.Type,
                state.ReplacedOld,
                state.ReplacedNew
            );
    }

    static bool StampsAdded(in TypeState state) =>
        state.AddTracked && ChangeOf(in state).StampAdded;

    static ComponentChange ChangeOf(in TypeState state) =>
        ComponentChange.Of(
            state.Before,
            state.EnableBefore,
            state.Present,
            state.Enable,
            readded: state.Before && state.Added,
            replaced: state.Replaced
        );

    // A recorded destroy is guarded once the batch's reparents have landed; one that breaks a Throw relation is spared and reported.
    void GuardRecordedDestroys()
    {
        provisional.Clear();
        SpareViolatingRoots(destroyedRoots, destroyedRoots.Count);
    }

    void ApplySide(Span<CommandRecord> records, ref int stale)
    {
        var log = attributed;
        foreach (var r in sideRecords)
        {
            ref readonly var record = ref records[r];
            indexAt = IndexPositionOf(records, r);
            Replay(log, records, r);
            var live = allocator.IsCurrent(record.Entity);
            if (
                !live
                || (record.Kind == CommandKind.Retarget && !allocator.IsCurrent(record.OldTarget))
            )
            {
                stale++;
                DropRecordedValue(in record);
            }
            else if (!record.Other.IsDefault && !allocator.IsCurrent(record.Other))
            {
                if (!OntoReleased(in record))
                    stale++;
                DropRecordedValue(in record);
            }
            else
                ApplyRecorded(in record);
            if (live)
                ClearPending(ref allocator.Slots[record.Entity.Index]);
        }
        log?.EndReplay();
        indexAt = 0;
    }

    // A write onto a released entity gets that entity's policy, as if it had landed before the release.
    bool OntoReleased(in CommandRecord record)
    {
        if (record.Kind == CommandKind.SetParent)
        {
            DestroyRecorded(record.Entity, LifecycleReason.Cascade);
            return true;
        }
        if (record.Kind is not (CommandKind.Relate or CommandKind.Retarget))
            return false;
        var store = RelationsOf(record.Type);
        if (store.Policy == TargetPolicy.Throw)
        {
            if (
                Unrelatable(
                    record.Type,
                    record.Entity,
                    MayLive(record.Entity),
                    record.Other,
                    false
                ) is
                { } error
            )
            {
                commandFailures.Add(error);
                return true;
            }
            if (record.Kind != CommandKind.Retarget)
                return false;
            store.Unrelate(this, record.Entity, record.OldTarget, default);
            return true;
        }
        if (record.Kind == CommandKind.Retarget)
            store.Unrelate(this, record.Entity, record.OldTarget, default);
        else if (store.Policy != TargetPolicy.DestroySource)
            return false;
        if (store.Policy == TargetPolicy.DestroySource)
            DestroyRecorded(record.Entity, LifecycleReason.Cascade);
        return true;
    }

    void DestroyRecorded(Entity entity, LifecycleReason reason)
    {
        ref var slot = ref allocator.Slots[entity.Index];
        if (!OwnsDestroy(slot))
            MarkDestroyed(entity, ref slot, reason, recorded: true);
    }

    void ApplyRecorded(in CommandRecord record)
    {
        var depth = untrackedDepth;
        untrackedDepth = record.Untracked ? 1 : 0;
        try
        {
            ApplySideRecord(in record);
        }
        catch (Exception error)
        {
            // Every store checks before it takes a recorded value, so a failed one still owns its buffers.
            DropRecordedValue(in record);
            commandFailures.Add(error);
        }
        finally
        {
            untrackedDepth = depth;
        }
    }

    void DropRecordedValue(in CommandRecord record)
    {
        if (OwnsValue(record.Kind) && record.Payload >= 0 && layout.OwnsBuffers[record.Type])
            FreeBuffers(
                record.Type,
                ref buffersById[record.Buffer].Payload(record.Type).RowRef(record.Payload)
            );
    }

    void ApplySideRecord(in CommandRecord record)
    {
        switch (record.Kind)
        {
            case CommandKind.Relate:
                RelationsOf(record.Type)
                    .ApplyRelate(
                        this,
                        record.Entity,
                        record.Other,
                        ref buffersById[record.Buffer].Payload(record.Type).RowRef(record.Payload),
                        default
                    );
                break;
            case CommandKind.Unrelate:
                relations[layout.RelationSlot[record.Type]]
                    ?.Unrelate(this, record.Entity, record.Other, default);
                break;
            case CommandKind.SetParent:
                Reparent(record.Entity, record.Other, record.Payload, default);
                break;
            case CommandKind.Destroy:
                DestroyRecorded(record.Entity, record.Reason);
                break;
            case CommandKind.CancelDestroy:
                ApplyCancel(record.Entity, ref allocator.Slots[record.Entity.Index]);
                break;
            case CommandKind.Retarget:
                RelationsOf(record.Type)
                    .ApplyRetarget(this, record.Entity, record.OldTarget, record.Other);
                break;
            case CommandKind.ClearTargets:
                RelationsOf(record.Type).ClearFrom(this, record.Entity, default);
                break;
            case CommandKind.ClearSources:
                RelationsOf(record.Type).ClearTo(this, record.Entity, default);
                break;
            case CommandKind.UpsertRow:
                RowsOf(record.Type)
                    .ApplyUpsert(
                        this,
                        record.Entity,
                        ref buffersById[record.Buffer].Payload(record.Type).RowRef(record.Payload),
                        default
                    );
                break;
            case CommandKind.RemoveRow:
                RowsOf(record.Type)
                    .ApplyRemove(
                        this,
                        record.Entity,
                        ref buffersById[record.Buffer].Payload(record.Type).RowRef(record.Payload)
                    );
                break;
        }
    }

    void Replaced(ref TypeState state, ref byte oldValue, ref byte newValue)
    {
        state.Replaced = true;
        (state.ReplacedOld, state.ReplacedNew) = observers.CaptureReplace(
            state.Type,
            ref oldValue,
            ref newValue
        );
    }

    void Emit(Span<CommandRecord> records)
    {
        var log = attributed;
        for (var i = 0; i < emissionCount; i++)
        {
            ref readonly var emission = ref emissions[i];
            ref readonly var fold = ref folds[emission.Fold];
            if (emission.Dead || fold.Cancelled)
                continue;
            if (log is not null)
                log.BeginReplay(TagOf(records[emissionRecords[i]]));
            var entity = fold.Entity;
            switch (emission.Kind)
            {
                case EmissionKind.Created:
                    changes.Append(LogKind.Created, entity, -1, default);
                    break;
                case EmissionKind.Component:
                {
                    ref readonly var state = ref states[emission.State];
                    var change = ChangeOf(in state);
                    if (change.LogRemoved)
                    {
                        Replay(log, records, state.RemovedBy);
                        changes.Append(LogKind.Removed, entity, state.Type, default);
                    }
                    Replay(log, records, state.ChangedBy);
                    LogAfterRemoval(change, entity, state.Type, default);
                    if (change.HearRemove)
                        observers.QueueCapturedRemove(entity, state.Type, state.Old, default);
                    if (change.HearAdd && observers.For(state.Type) is { WantsAdd: true })
                        observers.QueueAdd(
                            entity,
                            state.Type,
                            ref Bytes(ref StoredValue(entity, state.Type)),
                            default
                        );
                    if (change.HearReplace)
                        observers.QueueCapturedReplace(
                            entity,
                            state.Type,
                            state.ReplacedOld,
                            state.ReplacedNew
                        );
                    break;
                }
                case EmissionKind.Detached:
                case EmissionKind.Reattached:
                    var lifecycle =
                        emission.Kind == EmissionKind.Detached
                            ? LogKind.Detached
                            : LogKind.Reattached;
                    changes.Append(lifecycle, entity, -1, emission.Reason);
                    observers.QueueLifecycle(entity, lifecycle, emission.Reason);
                    break;
            }
        }
        log?.EndReplay();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Replay(AttributedLog? log, Span<CommandRecord> records, int record)
    {
        if (log is not null && record >= 0)
            log.BeginReplay(TagOf(records[record]));
    }

    WriteTag TagOf(in CommandRecord record) => buffersById[record.Buffer].tags[record.Sequence];
}
