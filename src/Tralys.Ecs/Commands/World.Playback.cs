using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

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
    readonly HashSet<Entity> sparedLogged = [];
    bool playing;
    internal PlaybackStats lastPlayback;

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
        public bool Destroy;
        public bool Revive;
        public bool Spared;
        public LifecycleReason DestroyReason;
        public sbyte Partition;
        public int PartitionEmission;
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

    internal void Submit(CommandBuffer buffer) => submitted.Add(buffer);

    /// <summary>Whether a non-finalising <see cref="Playback"/> has work: recorded commands to apply or observer events to deliver.</summary>
    public bool PlaybackOwed => !stage.IsEmpty || submitted.Count > 0 || observers.HasPending;

    void DiscardCommands()
    {
        stage.Clear();
        ForgetRecordedEdges();
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
    /// false, finalises pending destroys; observer work feeds back into the same playback until quiet.</summary>
    /// <exception cref="IndexConflictException">A unique index declared with <see cref="ConflictPolicy.Throw"/> saw a conflict; thrown
    /// once the playback has finished, so the world is consistent.</exception>
    /// <exception cref="CommandRejectedException">Buffered commands failed validation; they were dropped and the rest of the playback
    /// completed.</exception>
    /// <exception cref="TargetPolicyException">Finalising would release the target of a <see cref="TargetPolicy.Throw"/> relation whose
    /// source is live and not destroyed with it; nothing in that batch was released and its destroys stay pending.</exception>
    /// <exception cref="InvalidOperationException">A buffered command failed when applied, such as a parenting that would make a cycle; or
    /// a query is iterating; or an observer called <see cref="Playback"/>. Nothing was played back in the last two cases.</exception>
    /// <exception cref="AggregateException">More than one of the failures above, or an observer's exception together with any of
    /// them.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public void Playback(bool finalizeDestroys = true)
    {
        CheckWritable();
        if (playing)
            throw new InvalidOperationException(
                "Playback cannot run inside another playback, such as from an observer."
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
        if (
            failure is null
            && conflict is null
            && rejections.Count == 0
            && commandFailures.Count == 0
            && dangling is null
        )
        {
            lastPlayback = stats;
            return;
        }
        lastPlayback = stats;
        var errors = new List<Exception>(4);
        if (failure is not null)
            errors.Add(failure);
        errors.AddRange(commandFailures);
        commandFailures.Clear();
        if (conflict is not null)
            errors.Add(conflict);
        if (rejections.Count > 0)
            errors.Add(new CommandRejectedException([.. rejections]));
        if (dangling is not null)
            errors.Add(dangling);
        if (errors.Count > 1)
            throw new AggregateException("Playback failed in more than one way.", errors);
        ExceptionDispatchInfo.Throw(errors[0]);
    }

    PlaybackStats Play(bool finalize)
    {
        version++;
        rejections.Clear();
        commandFailures.Clear();
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
                delivered = observers.Deliver();
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
            sideRecords.Clear();
            stage.Clear();
            ForgetRecordedEdges();
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

    void FoldAndApply(Span<CommandRecord> records, ref int stale, ref int folded)
    {
        partitionCascades.Clear();
        Fold(records, ref stale, ref folded);
        Validate();
        Apply(records);
        FreeSuperseded();
        Emit(records);
        CascadePartitions();
    }

    readonly List<int> partitionCascades = [];

    // A moved entity's descendants follow it after the batch's own entries, as a cascade's consequences.
    void CascadePartitions()
    {
        foreach (var e in partitionCascades)
        {
            ref readonly var emission = ref emissions[e];
            foreach (var child in hierarchy.ChildrenOf(folds[emission.Fold].Entity))
                RefreshPartitions(child, emission.Reason);
        }
        partitionCascades.Clear();
    }

    /// <summary>Opens a batch of changes to <paramref name="entity"/> that land in one archetype move when it is disposed. Inside an
    /// observer, or while the entity already has changes waiting for playback, they wait for the next playback instead.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist, or a serial query body is running.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public EntityBatch Batch(Entity entity)
    {
        CheckWritable();
        CheckStructural();
        ref var slot = ref Slot(entity);
        if (Defers(slot))
            return new EntityBatch(this, entity, applies: false);
        slot.Pending = true;
        openBatches++;
        return new EntityBatch(this, entity, applies: true);
    }

    internal void CloseBatch(Entity entity)
    {
        openBatches--;
        ref var slot = ref allocator.Current(entity);
        if (Unsafe.IsNullRef(ref slot) || !slot.Pending || iterationDepth > 0 || playing)
            return;
        CheckWritable();
        var count = 0;
        for (var i = 0; i < stage.count; i++)
        {
            if (stage.records[i].Entity != entity)
                continue;
            // A reparent or an edge waits for the full playback, which applies side records.
            if (stage.records[i].Kind >= CommandKind.Relate)
                return;
            if (count == batchRecords.Length)
                Array.Resize(ref batchRecords, count * 2);
            batchRecords[count++] = stage.records[i];
        }
        IndexConflictException? conflict;
        BeginIndexBatch();
        try
        {
            int stale = 0,
                folded = 0;
            FoldAndApply(batchRecords.AsSpan(0, count), ref stale, ref folded);
            ApplySide(batchRecords.AsSpan(0, count), ref stale);
            GuardRecordedDestroys();
        }
        finally
        {
            sideRecords.Clear();
            allocator.Slots[entity.Index].Pending = false;
            stage.Drop(entity);
            overlay.Drop(entity.Index);
            conflict = EndIndexBatch();
        }
        if (commandFailures.Count == 0)
        {
            if (conflict is not null)
                throw conflict;
            return;
        }
        var spared = commandFailures[0];
        commandFailures.Clear();
        if (conflict is not null)
            throw new AggregateException(
                "Closing the batch failed in more than one way.",
                spared,
                conflict
            );
        ExceptionDispatchInfo.Throw(spared);
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
            if (
                record.Entity.IsPlaceholder
                && placeholders.TryGetValue(Key(record.Entity), out var real)
            )
                record.Entity = real;
            if (
                record.Other.IsPlaceholder
                && placeholders.TryGetValue(Key(record.Other), out var other)
            )
                record.Other = other;
            if (
                record.Kind is not (CommandKind.Set or CommandKind.Relate or CommandKind.UpsertRow)
                || record.Payload < 0
            )
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
            var f = FoldFor(record.Entity);
            ref var fold = ref folds[f];
            switch (record.Kind)
            {
                case CommandKind.Create:
                    fold.Created = true;
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
                    fold.DestroyReason = record.Reason;
                    if (attributed is not null)
                        TrackDestroy(f, r);
                    fold.Revive = false;
                    if (fold.Created)
                    {
                        fold.Cancelled = true;
                        folded++;
                    }
                    else
                        fold.Destroy = true;
                    break;
                case CommandKind.CancelDestroy:
                    if (
                        !fold.Created
                        && !OwnPendingAt(record.Entity)
                        && UnderPendingAt(record.Entity)
                    )
                        sideRecords.Add(r);
                    if (attributed is not null)
                        TrackDestroy(f, r);
                    if (fold.Cancelled)
                    {
                        fold.Cancelled = false;
                        folded--;
                    }
                    fold.Destroy = false;
                    fold.Revive = !fold.Created;
                    fold.DestroyReason = default;
                    break;
                case CommandKind.Detach:
                case CommandKind.Reattach:
                {
                    var detach = record.Kind == CommandKind.Detach;
                    fold.Partition = (sbyte)(detach ? 1 : 0);
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
                    if (!state.Present)
                    {
                        state.Present = true;
                        state.Added = true;
                        state.Unchecked = buffersById[record.Buffer].parallel;
                    }
                    state.Enable = true;
                    if (changes)
                        state.ChangedBy = r;
                    if (state.HasValue && layout.OwnsBuffers[record.Type])
                        Supersede(f, state);
                    state.HasValue = record.Payload >= 0;
                    state.Buffer = record.Buffer;
                    state.Payload = record.Payload;
                    state.Sequence = record.Sequence;
                    state.Tracked |= !record.Untracked;
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
                        // Hooks see the stored value before the staged copy, which may share its buffers, is dropped.
                        var staged = state;
                        state.HasValue = false;
                        if (WatchesRemoval(record.Type))
                        {
                            ref var departing = ref Departing(fold.Entity, state);
                            foreach (var hook in coreHooks)
                                hook.OnRemoving(this, fold.Entity, record.Type, ref departing);
                            if (layout.OwnsBuffers[record.Type] && !Unsafe.IsNullRef(ref departing))
                                FreeBuffers(record.Type, ref departing);
                        }
                        DropPayload(staged);
                    }
                    if (state.Before && state.RemovedBy < 0)
                        state.RemovedBy = r;
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
                            rejections.Add(
                                $"SetEnabled<{registry[record.Type].Name}>(true) on {record.Entity}, which has no {registry[record.Type].Name}; add it with Set or Add."
                            );
                        break;
                    }
                    if (state.Enable == enable)
                        break;
                    state.Tracked |= !record.Untracked;
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
                folded--;
            }
        }
    }

    // As of the records folded so far, so a recorded cancel decides as World.CancelDestroy would have at that point.
    bool OwnPendingAt(Entity entity)
    {
        if (foldOf[entity.Index] is var mark and > 0)
        {
            ref readonly var fold = ref folds[mark - 1];
            if (fold.Destroy)
                return true;
            if (fold.Revive)
                return false;
        }
        return allocator.Slots[entity.Index].State == LifecycleState.PendingDestroy;
    }

    bool UnderPendingAt(Entity entity)
    {
        for (var up = hierarchy.ParentOf(entity); !up.IsDefault; up = hierarchy.ParentOf(up))
        {
            if (OwnPendingAt(up))
                return true;
        }
        return false;
    }

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
            ref var stored = ref Departing(fold.Entity, created);
            created.Old = observers.CaptureDeparting(
                type,
                ref Unsafe.IsNullRef(ref stored) ? ref ZeroBytes.Value : ref stored
            );
        }
        fold.FirstState = stateCount;
        return ref states[stateCount++];
    }

    bool Differs(Entity entity, in TypeState state, in CommandRecord record)
    {
        if (record.Payload < 0)
            return false;
        var descriptor = registry[record.Type];
        ref var current = ref Departing(entity, state);
        if (descriptor.IsManaged || Unsafe.IsNullRef(ref current))
            return true;
        ref var next = ref buffersById[record.Buffer].Payload(record.Type).RowRef(record.Payload);
        return !MemoryMarshal
            .CreateReadOnlySpan(ref current, descriptor.Size)
            .SequenceEqual(MemoryMarshal.CreateReadOnlySpan(ref next, descriptor.Size));
    }

    ref byte Departing(Entity entity, in TypeState state)
    {
        if (state.HasValue)
            return ref buffersById[state.Buffer].Payload(state.Type).RowRef(state.Payload);
        ref var slot = ref allocator.Slots[entity.Index];
        switch (layout.Class[state.Type])
        {
            case StorageClass.Column:
            {
                var archetype = slot.Archetype!;
                var column = archetype.ColumnSlot(state.Type);
                return ref column >= 0
                    ? ref archetype.RowRef(column, slot.Row)
                    : ref Unsafe.NullRef<byte>();
            }
            case StorageClass.Sparse:
            {
                var storage = sparse[layout.SparseSlot[state.Type]]!;
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

        if (layout.SingletonCount > 0)
        {
            if (batchClaims.Length < layout.SingletonCount)
                batchClaims = new Entity[layout.SingletonCount];
            else
                Array.Clear(batchClaims);
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
                        Reject(ref state, SingletonConflict(state.Type, holder, entity.ToString()));
                }
            }
        }

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

    // A cancelled create bypasses finalisation, which destroys children and enforces Throw.
    bool CreateCancellable(Entity entity, Span<CommandRecord> records)
    {
        if (hierarchy.HasChildren(entity))
            return false;
        foreach (var r in sideRecords)
        {
            ref readonly var record = ref records[r];
            if (
                record.Other == entity
                && (
                    record.Kind == CommandKind.SetParent
                    || (
                        record.Kind is CommandKind.Relate or CommandKind.Retarget
                        && ThrowGuarded(record.Type)
                    )
                )
            )
                return false;
        }
        if (!guardsTargets)
            return true;
        foreach (var store in relations)
        {
            if (store is { Policy: TargetPolicy.Throw } && !store.SourcesOf(entity).IsEmpty)
                return false;
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
            slot.Pending = false;
            if (slot.State == LifecycleState.Reserved)
                Release(record.Entity, default);
        }
    }

    bool ThrowGuarded(int type) =>
        guardsTargets && registry[type].OnTargetDestroyed == TargetPolicy.Throw;

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
                Release(entity, fold.DestroyReason);
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
            var archetype = slot.Archetype!;
            if (fold.Partition >= 0)
                slot.DetachedSelf = fold.Partition == 1;
            if (fold.PartitionEmission >= 0)
            {
                var moved = slot.Detached != wasDetached;
                emissions[fold.PartitionEmission].Dead = !moved;
                if (moved)
                    hierarchy.RaiseAncestors(entity, version);
                if (moved && hierarchy.HasChildren(entity))
                    partitionCascades.Add(fold.PartitionEmission);
            }

            for (var s = fold.FirstState; s >= 0; s = states[s].Next)
                ApplyState(entity, ref slot, archetype, ref states[s]);

            if (fold.Destroy && slot.State == LifecycleState.Alive)
            {
                MarkPendingDestroy(entity, ref slot, fold.DestroyReason);
                destroyedRoots.Add(entity);
                log?.TagDestroy(entity.Index, TagOf(records[destroyRecords[f]]));
                if (hierarchy.HasChildren(entity))
                    RefreshChildren(entity);
            }
            else if (fold.Revive && slot.State == LifecycleState.PendingDestroy)
            {
                slot.State = LifecycleState.Alive;
                slot.DestroyReason = default;
                archetype.SetPendingDestroy(slot.Row, false);
                hierarchy.RaiseAncestors(entity, version);
                log?.TagDestroy(entity.Index, default);
                if (hierarchy.HasChildren(entity))
                    RefreshChildren(entity);
                fold.Spared = true;
            }
            slot.Pending = false;
        }
        LogSparedFolds(records);
        if (!hasIndexes)
            return;
        // Folds are in record order and the loop above is not; the order picks which entity a shared unique key goes to.
        for (var f = 0; f < foldCount; f++)
        {
            if (!folds[f].Cancelled)
                RefreshFoldIndexes(in folds[f]);
        }
    }

    // A spared root's subtree is only final once every fold has applied: a descendant destroyed in the same batch must not log as spared.
    void LogSparedFolds(Span<CommandRecord> records)
    {
        if (!changes.Logging)
            return;
        var log = attributed;
        for (var f = 0; f < foldCount; f++)
        {
            if (!folds[f].Spared)
                continue;
            if (log is not null)
                log.BeginReplay(TagOf(records[destroyRecords[f]]));
            LogSpared(folds[f].Entity, sparedLogged);
            log?.EndReplay();
        }
        sparedLogged.Clear();
    }

    // Runs once the slot is no longer pending, so the index reads stored values rather than the overlay.
    void RefreshFoldIndexes(in EntityFold fold)
    {
        if (fold.Created || fold.Destroy || fold.Revive || fold.Partition >= 0)
        {
            RefreshAllIndexes(fold.Entity);
            return;
        }
        for (var s = fold.FirstState; s >= 0; s = states[s].Next)
            RefreshIndexes(fold.Entity, states[s].Type);
    }

    Archetype TargetOf(ref EntityFold fold)
    {
        var source = fold.Source >= 0 ? archetypes[fold.Source] : root;
        var detached = source.Detached;
        if (fold.Partition >= 0 || (fold.Created && partitionsUsed))
        {
            var self =
                fold.Partition >= 0
                    ? fold.Partition == 1
                    : allocator.Slots[fold.Entity.Index].DetachedSelf;
            detached = self || InheritsDetach(fold.Entity);
        }
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

    bool MovesArchetype(in TypeState state) =>
        state.Present != state.Before
        && layout.Class[state.Type] is StorageClass.Column or StorageClass.SignatureTag;

    unsafe void ApplyState(
        Entity entity,
        ref EntitySlot slot,
        Archetype archetype,
        ref TypeState state
    )
    {
        var type = state.Type;
        var row = slot.Row;
        var added = state.Added;
        var track = Tracking && state.Tracked;
        if (!state.Present)
            ReleaseSingleton(entity, type);
        else if (added)
            HoldSingleton(entity, type);
        switch (layout.Class[type])
        {
            case StorageClass.Column:
            case StorageClass.SignatureTag:
            {
                if (!state.Present)
                    return;
                ref readonly var typeSlot = ref archetype.Map.SlotAt(archetype.Map.IndexOf(type));
                var tracked = track ? typeSlot.Tracked : -1;
                if (typeSlot.Column >= 0 && state.HasValue)
                {
                    var payloads = buffersById[state.Buffer].Payload(type);
                    var column = archetype.Columns[typeSlot.Column];
                    if (!added && layout.OwnsBuffers[type])
                        FreeReplacedBuffers(
                            type,
                            ref archetype.RowRef(typeSlot.Column, row),
                            ref payloads.RowRef(state.Payload)
                        );
                    bool changed;
                    if (!added && layout.ReplaceOnly[type])
                    {
                        ref var incoming = ref payloads.RowRef(state.Payload);
                        ref var cell = ref archetype.RowRef(typeSlot.Column, row);
                        changed = !registry[type].Ops.ValuesEqual(ref cell, ref incoming);
                        if (changed)
                        {
                            Replaced(ref state, ref cell, ref incoming);
                            payloads.CopyToColumn(state.Payload, column, row);
                        }
                    }
                    else
                        changed = payloads.WriteToColumn(state.Payload, column, row, !added);
                    if (changed && tracked >= 0 && !added)
                        archetype.Stamp(tracked, row, version);
                }
                if (added && tracked >= 0)
                    archetype.StampAdded(tracked, row, version);
                var enable = typeSlot.Enable;
                if (enable >= 0 && archetype.IsEnabled(enable, row) != state.Enable)
                {
                    archetype.SetEnabled(enable, row, state.Enable);
                    if (tracked >= 0)
                        archetype.Stamp(tracked, row, version);
                    if (layout.InheritedSlot[type] > 0 && hierarchy.HasChildren(entity))
                        RefreshChildren(entity);
                }
                return;
            }
            case StorageClass.Sparse:
            {
                var descriptor = registry[type];
                var storage = SparseFor(type);
                if (!state.Present)
                {
                    if (storage.Remove(entity.Index))
                        slot.SparseCount--;
                    return;
                }
                if (!state.HasValue)
                    return;
                ref var incoming = ref buffersById[state.Buffer]
                    .Payload(type)
                    .RowRef(state.Payload);
                var stamp = track && descriptor.IsTracked;
                int at;
                if (!state.Before)
                {
                    at = storage.AddFrom(entity, ref incoming);
                    slot.SparseCount++;
                }
                else
                {
                    at = storage.SlotOf(entity.Index);
                    if (!added)
                    {
                        ref var current = ref storage.RowRef(at);
                        if (descriptor.Ops.ValuesEqual(ref current, ref incoming))
                            return;
                        if (layout.ReplaceOnly[type])
                            Replaced(ref state, ref current, ref incoming);
                        if (layout.OwnsBuffers[type])
                            FreeReplacedBuffers(type, ref current, ref incoming);
                        storage.WriteFrom(at, ref incoming);
                        if (stamp)
                            storage.Stamp(at, version);
                        return;
                    }
                    storage.WriteFrom(at, ref incoming);
                }
                if (stamp)
                    storage.StampAdded(at, version);
                return;
            }
            case StorageClass.SparseTag:
                if (
                    (sparseTags[layout.SparseTagSlot[type]] ??= new SparseTagStorage(type)).Set(
                        entity.Index,
                        state.Present
                    )
                )
                    slot.SparseCount = (ushort)(slot.SparseCount + (state.Present ? 1 : -1));
                return;
        }
    }

    readonly List<Entity> destroyedRoots = [];

    // A recorded destroy is guarded once the batch's reparents have landed, against the state its call would have seen. One that
    // breaks a Throw relation is spared and reported, so it never fails a later finalisation.
    void GuardRecordedDestroys()
    {
        if (guardsTargets && AnyThrowEdge())
        {
            bool spared;
            // A spare revives a source that an earlier root's check counted as dying.
            do
            {
                spared = false;
                foreach (var root in destroyedRoots)
                {
                    ref var slot = ref allocator.Current(root);
                    if (Unsafe.IsNullRef(ref slot) || slot.State != LifecycleState.PendingDestroy)
                        continue;
                    try
                    {
                        CheckSurvivingSources(root);
                    }
                    catch (TargetPolicyException error)
                    {
                        commandFailures.Add(error);
                        Spare(root, ref slot);
                        spared = true;
                    }
                }
            } while (spared);
        }
        destroyedRoots.Clear();
    }

    void ApplySide(Span<CommandRecord> records, ref int stale)
    {
        var log = attributed;
        foreach (var r in sideRecords)
        {
            ref readonly var record = ref records[r];
            Replay(log, records, r);
            var live = allocator.IsCurrent(record.Entity);
            if (!live || (!record.Other.IsDefault && !allocator.IsCurrent(record.Other)))
            {
                stale++;
                DropRecordedValue(in record);
            }
            else
                ApplyRecorded(in record);
            if (live)
                allocator.Slots[record.Entity.Index].Pending = false;
        }
        log?.EndReplay();
    }

    void ApplyRecorded(in CommandRecord record)
    {
        if (record.Untracked)
            untrackedDepth++;
        try
        {
            ApplySideRecord(in record);
        }
        catch (Exception error)
        {
            commandFailures.Add(error);
        }
        finally
        {
            if (record.Untracked)
                untrackedDepth--;
        }
    }

    void DropRecordedValue(in CommandRecord record)
    {
        if (
            record.Kind is CommandKind.Set or CommandKind.Relate or CommandKind.UpsertRow
            && record.Payload >= 0
            && layout.OwnsBuffers[record.Type]
        )
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
                Reparent(record.Entity, record.Other, record.Payload, default, guardTargets: false);
                break;
            case CommandKind.CancelDestroy:
                Reparent(record.Entity, default, -1, default, guardTargets: false);
                break;
            case CommandKind.Retarget:
                var oldTarget = buffersById[record.Buffer].retargets[record.Payload];
                if (allocator.IsCurrent(oldTarget))
                    RelationsOf(record.Type)
                        .ApplyRetarget(this, record.Entity, oldTarget, record.Other);
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
                    var change = ComponentChange.Of(
                        state.Before,
                        state.EnableBefore,
                        state.Present,
                        state.Enable,
                        readded: state.Before && state.Added,
                        replaced: state.Replaced
                    );
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
                            ref StoredRef(entity, state.Type),
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

    ref byte StoredRef(Entity entity, int type)
    {
        ref var slot = ref allocator.Slots[entity.Index];
        switch (layout.Class[type])
        {
            case StorageClass.Column:
            {
                var archetype = slot.Archetype!;
                return ref archetype.RowRef(archetype.ColumnSlot(type), slot.Row);
            }
            case StorageClass.Sparse:
            {
                var storage = sparse[layout.SparseSlot[type]]!;
                return ref storage.RowRef(storage.SlotOf(entity.Index));
            }
        }
        return ref ZeroBytes.Value;
    }
}
