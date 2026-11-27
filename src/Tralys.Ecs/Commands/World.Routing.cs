using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    internal int iterationDepth;

    // Serial walks only; iterationDepth also counts observer delivery.
    internal int walkDepth;
    bool walkApplies;

    readonly HashSet<(uint Entity, int Type)> recordedSources = [];
    readonly HashSet<(uint Entity, int Type)> recordedTargets = [];
    readonly HashSet<(uint Entity, int Type)> waitingRows = [];

    // A cancel under a dying ancestor records a move to no parent, since it unparents when it applies.
    readonly Dictionary<uint, Entity> recordedParents = [];

    // A walk and a frozen plan hold every write that reshapes; observer delivery holds only component structure, creates and detaches.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool Holds(bool byDelivery) =>
        walkDepth > 0 || planFrozen || (byDelivery && iterationDepth > 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Waits(in EntitySlot slot) => slot.Pending || slot.State == LifecycleState.Reserved;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool RecordsStructure(in EntitySlot slot) => Holds(byDelivery: true) || Waits(slot);

    // A value write lands at once unless only its enable flip is recorded; an add, remove or flip waits whenever the entity's structure does.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool Records(in EntitySlot slot, in ValueLocation location, bool structural) =>
        structural ? RecordsStructure(slot) : location.FlipRecorded;

    bool RecordsCreate => Holds(byDelivery: true);

    bool RecordsLifecycle(in EntitySlot slot) => Holds(byDelivery: false) || Waits(slot);

    bool RecordsEdge(Entity source, Entity target, int type) =>
        EdgeWaits(source, target, type) || Holds(byDelivery: false);

    // Held, a write records only when it adds or removes the edge; a payload change of an existing one lands at once.
    bool RecordsEdge<R>(Entity source, Entity target, int type, bool adds)
        where R : struct, IRelation =>
        EdgeWaits(source, target, type)
        || (Holds(byDelivery: false) && HasRelation<R>(source, target) != adds);

    internal bool RecordsRow(Entity owner, int type, bool reshapes) =>
        RowsWait(owner, type) || (Holds(byDelivery: false) && reshapes);

    internal bool RecordsRow<TKey, TRow>(Entity owner, int type, in TKey key, bool adds)
        where TKey : notnull
        where TRow : struct, IRow<TKey> =>
        RowsWait(owner, type)
        || (Holds(byDelivery: false) && HasRow<TKey, TRow>(owner, key) != adds);

    bool HasRow<TKey, TRow>(Entity owner, in TKey key)
        where TKey : notnull
        where TRow : struct, IRow<TKey> =>
        FindRows<TKey, TRow>() is { } store
        && store.BlockOf(owner) is { } block
        && store.Find(block, key) >= 0;

    // The latest waiting write that names the edge decides; without one, storage does.
    bool RelatedAsRecorded<R>(Entity source, Entity target, int type)
        where R : struct, IRelation
    {
        var symmetric = (registry[type].RelationTraits & RelationTraits.Symmetric) != 0;
        bool Names(Entity from, Entity to) =>
            (from == source && to == target) || (symmetric && from == target && to == source);
        bool Ends(Entity end, Entity one, Entity other) =>
            end == one || (symmetric && end == other);
        for (var i = stage.count - 1; i >= 0; i--)
        {
            ref readonly var record = ref stage.records[i];
            if (record.Type != type)
                continue;
            switch (record.Kind)
            {
                case CommandKind.Relate when Names(record.Entity, record.Other):
                    return true;
                case CommandKind.Unrelate when Names(record.Entity, record.Other):
                    return false;
                case CommandKind.Retarget when Names(record.Entity, record.Other):
                    return true;
                case CommandKind.Retarget when Names(record.Entity, record.OldTarget):
                    return false;
                case CommandKind.ClearTargets when Ends(record.Entity, source, target):
                    return false;
                case CommandKind.ClearSources when Ends(record.Entity, target, source):
                    return false;
            }
        }
        return HasRelation<R>(source, target);
    }

    bool Reserved(Entity entity) =>
        !entity.IsDefault
        && allocator.IsCurrent(entity)
        && allocator.Slots[entity.Index].State == LifecycleState.Reserved;

    bool SourceWaits(Entity source, int type) =>
        !source.IsDefault
        && (
            Reserved(source)
            || (recordedSources.Count > 0 && recordedSources.Contains((source.Index, type)))
        );

    bool TargetWaits(Entity target, int type) =>
        !target.IsDefault
        && (
            Reserved(target)
            || (recordedTargets.Count > 0 && recordedTargets.Contains((target.Index, type)))
        );

    bool EdgeWaits(Entity source, Entity target, int type) =>
        SourceWaits(source, type) || TargetWaits(target, type);

    internal bool RowsWait(Entity owner, int type) =>
        Reserved(owner) || (waitingRows.Count > 0 && waitingRows.Contains((owner.Index, type)));

    void NoteRecordedEdge(Entity source, Entity target, int type)
    {
        recordedSources.Add((source.Index, type));
        if (!target.IsDefault)
        {
            recordedTargets.Add((target.Index, type));
            if ((registry[type].RelationTraits & RelationTraits.Symmetric) != 0)
            {
                recordedSources.Add((target.Index, type));
                recordedTargets.Add((source.Index, type));
            }
        }
        MarkPending(ref allocator.Slots[source.Index]);
    }

    void NoteRecordedSources(Entity target, int type)
    {
        recordedTargets.Add((target.Index, type));
        MarkPending(ref allocator.Slots[target.Index]);
    }

    void Stage(
        CommandKind kind,
        Entity entity,
        int type = -1,
        int payload = -1,
        LifecycleReason reason = default
    )
    {
        var index = entity.Index;
        ref var slot = ref allocator.Slots[index];
        switch (kind)
        {
            case CommandKind.Set:
                overlay.Set(index, type, true, payload);
                overlay.SetEnabled(index, type, true);
                break;
            case CommandKind.Remove:
                overlay.Set(index, type, false, -1);
                break;
            case CommandKind.Enable or CommandKind.Disable:
                overlay.Flip(index, type, kind == CommandKind.Enable);
                break;
            case CommandKind.Destroy or CommandKind.CancelDestroy:
                overlay.Mark(index, PendingOverlay.Destroying, kind == CommandKind.Destroy);
                break;
            case CommandKind.Detach or CommandKind.Reattach:
                overlay.Mark(index, PendingOverlay.Detaching, kind == CommandKind.Detach);
                break;
        }
        stage.Record(kind, entity, type, payload, reason);
        MarkPending(ref slot);
    }

    // Spares what reads see dying: its own destroy, or an ancestor's, which leaves the entity rescued.
    bool StageCancel(Entity entity)
    {
        var own = overlay.Marked(
            entity.Index,
            PendingOverlay.Destroying,
            OwnsDestroy(allocator.Slots[entity.Index])
        );
        var rescues = !own && AwayAsRecorded(entity, dying: true, detached: false);
        Stage(CommandKind.CancelDestroy, entity);
        if (rescues)
            recordedParents[entity.Index] = default;
        return own || rescues;
    }

    internal void NoteRecordedParent(Entity child, Entity parent) =>
        recordedParents[child.Index] = parent;

    internal Entity ParentAsRecorded(Entity entity) =>
        recordedParents.Count > 0 && recordedParents.TryGetValue(entity.Index, out var parent)
            ? parent
            : hierarchy.ParentOf(entity);

    internal void StageSet<T>(Entity entity, int type, in T value)
        where T : struct => Stage(CommandKind.Set, entity, type, stage.PayloadFor(type, value));

    internal void EditedRecorded(Entity entity, int type)
    {
        if (Tracking)
            stage.EditedTracked(entity, type);
    }

    internal int RecordRow<TRow>(Entity owner, int type, CommandKind kind, in TRow row)
        where TRow : struct
    {
        var payload = stage.RecordValue(kind, owner, row);
        waitingRows.Add((owner.Index, type));
        return payload;
    }

    internal void ForgetWaiting()
    {
        recordedSources.Clear();
        recordedTargets.Clear();
        waitingRows.Clear();
        recordedParents.Clear();
    }

    // Not submitted: parallel commands apply after every serial write, a walk's included.
    bool WalkMayApply() => iterationDepth == 0 && !playing && openBatches == 0 && stage.IsEmpty;

    internal IterationScope BeginIteration()
    {
        if (walkDepth == 0)
            walkApplies = WalkMayApply();
        iterationDepth++;
        walkDepth++;
        return new IterationScope(this, walk: true);
    }

    internal IterationScope BeginDelivery()
    {
        iterationDepth++;
        return new IterationScope(this, walk: false);
    }

    internal ref struct IterationScope(World world, bool walk)
    {
        World? world = world;
        Exception? fault;

        public void Fault(Exception error) => fault = error;

        public void Dispose()
        {
            if (world is not { } closing)
                return;
            world = null;
            if (closing.iterationDepth > 0)
                closing.iterationDepth--;
            if (
                walk
                && closing.walkDepth > 0
                && --closing.walkDepth == 0
                && closing.walkApplies
                && closing.iterationDepth == 0
            )
                closing.ApplyWalk(fault);
        }
    }
}
