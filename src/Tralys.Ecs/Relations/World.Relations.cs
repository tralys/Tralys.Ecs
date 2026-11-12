using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>Thrown when an operation would leave a <see cref="TargetPolicy.Throw"/> relation's live source pointing at a destroyed
/// target: destroying the target, parenting it under a dying entity, or relating to a dying entity. Nothing changed.</summary>
public sealed class TargetPolicyException(string message) : InvalidOperationException(message);

public sealed partial class World
{
    internal readonly RelationStore?[] relations;
    readonly bool guardsTargets;
    readonly List<Entity> doomed = [];
    int[] doomedWaves = [];

    RelationStore<R>? FindRelations<R>()
        where R : struct, IRelation =>
        Unsafe.As<RelationStore<R>?>(relations[layout.RelationSlot[registry.IndexOf<R>()]]);

    internal unsafe RelationStore RelationsOf(int type)
    {
        var slot = layout.RelationSlot[type];
        if (slot < 0)
            throw new InvalidOperationException($"{registry[type].Name} is not a relation.");
        return relations[slot] ??= registry[type].Ops.CreateRelation(type, registry[type]);
    }

    RelationStore<R> RelationsOf<R>()
        where R : struct, IRelation =>
        Unsafe.As<RelationStore<R>>(RelationsOf(registry.IndexOf<R>()));

    /// <summary>Adds the edge <paramref name="source"/> → <paramref name="target"/>, or, when the edge exists with a payload whose bytes
    /// differ, replaces the payload and logs <see cref="LogKind.RelationChanged"/>. An exclusive relation first drops the source's other
    /// target; a symmetric one adds the mirror edge too.</summary>
    [Effect(Effects.Mutates)]
    public void Relate<R>(
        Entity source,
        Entity target,
        in R payload = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where R : struct, IRelation
    {
        At(file, line);
        RelateEdge(source, target, payload);
    }

    void RelateEdge<R>(Entity source, Entity target, in R payload)
        where R : struct, IRelation
    {
        CheckWritable();
        var type = registry.IndexOf<R>();
        if (RecordsEdge(source, target, type))
        {
            RelatableStore<R>(source, target);
            stage.RecordValue(CommandKind.Relate, source, payload, target);
            NoteRecordedEdge(source, target, type);
            return;
        }
        RelatableStore<R>(source, target).Relate(this, source, target, payload);
    }

    // Later edge writes on an end with an edge waiting wait too, or they would apply ahead of it.
    readonly HashSet<(uint Entity, int Type)> recordedSources = [];
    readonly HashSet<(uint Entity, int Type)> recordedTargets = [];

    bool RecordsEdge(Entity source, Entity target, int type) =>
        Reserved(source)
        || Reserved(target)
        || (
            recordedSources.Count > 0
            && (
                recordedSources.Contains((source.Index, type))
                || (!target.IsDefault && recordedTargets.Contains((target.Index, type)))
            )
        );

    bool Reserved(Entity entity) =>
        !entity.IsDefault
        && allocator.IsCurrent(entity)
        && allocator.Slots[entity.Index].State == LifecycleState.Reserved;

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
        allocator.Slots[source.Index].Pending = true;
    }

    internal bool RecordsRow<TRow>(Entity owner, CommandKind kind, in TRow row)
        where TRow : struct
    {
        if (!Reserved(owner))
            return false;
        stage.RecordValue(kind, owner, row);
        return true;
    }

    internal void CheckRowsExist(Entity owner)
    {
        if (Reserved(owner))
            throw new InvalidOperationException(
                $"{owner} was created by an observer and exists at playback; write its rows with Set and Remove, which wait for it."
            );
    }

    internal void ForgetRecordedEdges()
    {
        recordedSources.Clear();
        recordedTargets.Clear();
    }

    RelationStore<R> RelatableStore<R>(Entity source, Entity target)
        where R : struct, IRelation
    {
        Slot(source);
        Slot(target);
        var store = RelationsOf<R>();
        if (store.Policy != TargetPolicy.Throw)
            return store;
        if (!IsLive(target) && IsLive(source))
            ThrowDyingTarget<R>(target);
        if (store.Symmetric && !IsLive(source) && IsLive(target))
            ThrowDyingTarget<R>(source);
        return store;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowDyingTarget<R>(Entity target) =>
        throw new TargetPolicyException(
            $"{target} is being destroyed and {typeof(R).Name} forbids destroying a target with surviving sources."
        );

    /// <summary>Removes the edge <paramref name="source"/> → <paramref name="target"/>, and its mirror for a symmetric relation; false
    /// when there is none. Applies at once, even inside a query body, unless either end was created by an observer or has an edge
    /// waiting, when it waits too and returns false.</summary>
    [Effect(Effects.Mutates)]
    public bool Unrelate<R>(
        Entity source,
        Entity target,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where R : struct, IRelation
    {
        At(file, line);
        CheckWritable();
        var type = registry.IndexOf<R>();
        if (RecordsEdge(source, target, type))
        {
            stage.Record(CommandKind.Unrelate, source, type, other: target);
            NoteRecordedEdge(source, target, type);
            return false;
        }
        return FindRelations<R>()?.Unrelate(this, source, target, default) == true;
    }

    /// <summary>Moves the edge to <paramref name="newTarget"/>, keeping its payload.</summary>
    [Effect(Effects.Mutates)]
    public void Retarget<R>(
        Entity source,
        Entity oldTarget,
        Entity newTarget,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where R : struct, IRelation
    {
        At(file, line);
        CheckWritable();
        var type = registry.IndexOf<R>();
        if (RecordsEdge(source, oldTarget, type) || RecordsEdge(source, newTarget, type))
        {
            stage.Record(
                CommandKind.Retarget,
                source,
                type,
                payload: stage.Retarget(oldTarget),
                other: newTarget
            );
            NoteRecordedEdge(source, oldTarget, type);
            NoteRecordedEdge(source, newTarget, type);
            return;
        }
        var store = FindRelations<R>();
        var at = store?.IndexOf(source, oldTarget) ?? -1;
        if (at < 0)
            throw new InvalidOperationException(
                $"{source} has no {typeof(R).Name} edge to {oldTarget}."
            );
        RelatableStore<R>(source, newTarget);
        var payload = store!.EdgesOf(source)[at].Payload;
        store.Retarget(this, source, oldTarget, newTarget, payload);
    }

    /// <summary>The <typeparamref name="R"/> edge <paramref name="source"/> → <paramref name="target"/> exists.</summary>
    [Effect(Effects.None)]
    public bool HasRelation<R>(Entity source, Entity target)
        where R : struct, IRelation
    {
        return (FindRelations<R>()?.IndexOf(source, target) ?? -1) >= 0;
    }

    /// <summary>The payload of the <typeparamref name="R"/> edge from <paramref name="source"/> to <paramref name="target"/>.</summary>
    /// <returns>False when there is no such edge.</returns>
    [Effect(Effects.None)]
    public bool TryGetRelation<R>(Entity source, Entity target, out R payload)
        where R : struct, IRelation
    {
        if (FindRelations<R>() is { } store && store.IndexOf(source, target) is var at and >= 0)
        {
            payload = store.EdgesOf(source)[at].Payload;
            return true;
        }
        payload = default;
        return false;
    }

    /// <summary>The targets of <paramref name="source"/>'s <typeparamref name="R"/> edges, in the order they were related; empty when it
    /// has none. The list throws once an edge of <typeparamref name="R"/> from <paramref name="source"/> is made or broken, though one
    /// read while it had none may stay empty.</summary>
    [Effect(Effects.None)]
    public RelationTargets<R> Targets<R>(Entity source)
        where R : struct, IRelation
    {
        return FindRelations<R>() is { } store && store.EdgeListOf(source) is var list and >= 0
            ? new(store.EdgeLists, list)
            : default;
    }

    /// <summary>The entities with an edge to <paramref name="target"/>, in the order they were related. The list throws once an edge of
    /// <typeparamref name="R"/> to <paramref name="target"/> is made or broken, though one read while it had none may stay empty; take <see cref="SnapshotSources{R}"/> to edit it while
    /// walking.</summary>
    [Effect(Effects.None)]
    public EntityList Sources<R>(Entity target)
        where R : struct, IRelation
    {
        return FindRelations<R>() is { } store ? store.ListSources(target) : default;
    }

    /// <summary>Removes every <typeparamref name="R"/> edge from <paramref name="source"/>.</summary>
    [Effect(Effects.Mutates)]
    public void ClearTargets<R>(
        Entity source,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where R : struct, IRelation
    {
        At(file, line);
        CheckWritable();
        var type = registry.IndexOf<R>();
        if (RecordsEdge(source, default, type))
        {
            stage.Record(CommandKind.ClearTargets, source, type);
            NoteRecordedEdge(source, default, type);
            return;
        }
        FindRelations<R>()?.ClearFrom(this, source, default);
    }

    /// <summary>Removes every <typeparamref name="R"/> edge to <paramref name="target"/>.</summary>
    [Effect(Effects.Mutates)]
    public void ClearSources<R>(
        Entity target,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where R : struct, IRelation
    {
        At(file, line);
        CheckWritable();
        var type = registry.IndexOf<R>();
        if (Reserved(target) || recordedTargets.Contains((target.Index, type)))
        {
            stage.Record(CommandKind.ClearSources, target, type);
            recordedTargets.Add((target.Index, type));
            allocator.Slots[target.Index].Pending = true;
            return;
        }
        FindRelations<R>()?.ClearTo(this, target, default);
    }

    internal ulong RelatedVersionOf<R>(Entity source)
        where R : struct, IRelation => FindRelations<R>()?.RelatedVersionOf(source) ?? 0;

    bool HasThrowRelation()
    {
        for (var type = 0; type < registry.IndexBound; type++)
        {
            if (
                layout.RelationSlot[type] >= 0
                && registry[type].OnTargetDestroyed == TargetPolicy.Throw
            )
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void CheckTargetPolicy(Entity root)
    {
        if (guardsTargets && IsLive(root) && AnyThrowEdge())
            CheckSurvivingSources(root);
    }

    bool AnyThrowEdge()
    {
        foreach (var store in relations)
        {
            if (store is { Policy: TargetPolicy.Throw, HasTargets: true })
                return true;
        }
        return false;
    }

    void CheckSurvivingSources(Entity root)
    {
        BeginDoomed();
        DoomSubtree(root, 1);
        DoomCascades();
        ThrowOnSurvivor(finalising: false);
    }

    void CheckFinalisable(int batch)
    {
        BeginDoomed();
        for (var i = 0; i < batch; i++)
        {
            var root = pendingDestroys[i];
            ref var slot = ref allocator.Current(root);
            if (!Unsafe.IsNullRef(ref slot) && slot.State == LifecycleState.PendingDestroy)
                DoomSubtree(root, 1);
        }
        DoomCascades();
        ThrowOnSurvivor(finalising: true);
    }

    void CheckFinalising()
    {
        BeginDoomed();
        foreach (var entity in finalising)
        {
            ref var slot = ref allocator.Current(entity);
            if (!Unsafe.IsNullRef(ref slot) && slot.State == LifecycleState.PendingDestroy)
                Doom(entity, 1);
        }
        ThrowOnSurvivor(finalising: true);
    }

    void Doom(Entity entity, int wave)
    {
        doomedWaves[entity.Index] = wave;
        doomed.Add(entity);
    }

    void BeginDoomed()
    {
        if (doomedWaves.Length < allocator.Capacity)
            Array.Resize(ref doomedWaves, Math.Max(allocator.Capacity, doomedWaves.Length * 2));
    }

    void DoomSubtree(Entity root, int wave)
    {
        var count = 0;
        Push(ref count, root);
        while (count > 0)
        {
            var entity = walk[--count];
            if (doomedWaves[entity.Index] != 0)
                continue;
            Doom(entity, wave);
            foreach (var child in hierarchy.ChildrenOf(entity))
                Push(ref count, child);
        }
    }

    void DoomCascades()
    {
        for (var i = 0; i < doomed.Count; i++)
        {
            var next = doomedWaves[doomed[i].Index] + 1;
            foreach (var store in relations)
            {
                if (store is not { Policy: TargetPolicy.DestroySource })
                    continue;
                foreach (var source in store.SourcesOf(doomed[i]))
                    DoomSubtree(source, next);
            }
        }
    }

    // Same batch only: a cascade releases a batch later, and a pending source can still be rescued.
    void ThrowOnSurvivor(bool finalising)
    {
        foreach (var store in relations)
        {
            if (store is not { Policy: TargetPolicy.Throw })
                continue;
            foreach (var target in doomed)
            {
                foreach (var source in store.SourcesOf(target))
                {
                    if (
                        doomedWaves[source.Index] == doomedWaves[target.Index]
                        || (finalising ? !allocator.IsCurrent(source) : !IsLive(source))
                    )
                        continue;
                    EndDoomed();
                    var name = registry[store.Type].Name;
                    throw new TargetPolicyException(
                        $"{target} is the target of {name} from {source}, which outlives it; {name} forbids destroying a target with surviving sources."
                    );
                }
            }
        }
        EndDoomed();
    }

    void EndDoomed()
    {
        foreach (var entity in doomed)
            doomedWaves[entity.Index] = 0;
        doomed.Clear();
    }

    internal void Propagate(Entity entity, int type, ulong at)
    {
        if (layout.PropagatesToAncestors[type] && !hierarchy.IsEmpty)
            hierarchy.RaiseAncestors(entity, at);
        if (!layout.PropagatesToSources[type])
            return;
        foreach (var store in relations)
            store?.RaiseSources(entity, at);
    }
}
