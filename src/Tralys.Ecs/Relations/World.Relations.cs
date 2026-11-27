using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>Thrown when an operation would leave a <see cref="TargetPolicy.Throw"/> relation's live source pointing at a destroyed
/// target: destroying the target, parenting it under a dying entity, relating to a dying entity, or cancelling the destroy of a source
/// whose target is dying. Nothing changed; from a playback, the command was dropped or the destroy spared.</summary>
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
    /// target; a symmetric one adds the mirror edge too. Inside a query walk a new edge waits until the walk closes; a payload change
    /// applies at once.</summary>
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
        if (RecordsEdge<R>(source, target, type, adds: true))
        {
            RelatableStore<R>(source, target);
            CheckRelatable(type, source, target, asRecorded: true);
            stage.RecordValue(CommandKind.Relate, source, payload, target);
            NoteRecordedEdge(source, target, type);
            return;
        }
        RelatableStore<R>(source, target).Relate(this, source, target, payload);
        VerifyInvariants();
    }

    RelationStore<R> RelatableStore<R>(Entity source, Entity target)
        where R : struct, IRelation
    {
        Slot(source);
        Slot(target);
        return RelationsOf<R>();
    }

    internal void CheckRelatable(int type, Entity source, Entity target, bool asRecorded = false)
    {
        var lives = asRecorded
            ? (LivesAsRecorded(source), LivesAsRecorded(target))
            : (MayLive(source), MayLive(target));
        if (Unrelatable(type, source, lives.Item1, target, lives.Item2) is { } error)
            throw error;
    }

    internal TargetPolicyException? Unrelatable(
        int type,
        Entity source,
        bool sourceLives,
        Entity target,
        bool targetLives
    )
    {
        var descriptor = registry[type];
        if (descriptor.OnTargetDestroyed != TargetPolicy.Throw)
            return null;
        if (sourceLives && !targetLives)
            return DyingTarget(descriptor, target);
        if (
            (descriptor.RelationTraits & RelationTraits.Symmetric) != 0
            && targetLives
            && !sourceLives
        )
            return DyingTarget(descriptor, source);
        return null;
    }

    static TargetPolicyException DyingTarget(ComponentDescriptor relation, Entity target) =>
        new(
            $"{target} is being destroyed and {relation.Name} forbids destroying a target with surviving sources."
        );

    bool MayLive(Entity entity) => IsLive(entity) || DyingProvisionally(entity);

    bool LivesAsRecorded(Entity entity) =>
        allocator.IsCurrent(entity) && !AwayAsRecorded(entity, dying: true, detached: false);

    bool DyingProvisionally(Entity entity)
    {
        if (provisional.Count == 0 || !allocator.IsCurrent(entity))
            return false;
        var dying = false;
        for (var node = entity; !node.IsDefault; node = hierarchy.ParentOf(node))
        {
            if (allocator.Slots[node.Index].State != LifecycleState.PendingDestroy)
                continue;
            if (!provisional.Contains(node))
                return false;
            dying = true;
        }
        return dying;
    }

    /// <summary>Removes the edge <paramref name="source"/> → <paramref name="target"/>, and its mirror for a symmetric relation; false
    /// when there is none. Applies at once unless a query walk is open, either end was created by an observer, or either end has an edge
    /// waiting; then it waits too, until the walk closes or the next playback, and returns whether reads see the edge now.</summary>
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
        if (!allocator.IsCurrent(source) || !allocator.IsCurrent(target))
            return false;
        var type = registry.IndexOf<R>();
        if (RecordsEdge<R>(source, target, type, adds: false))
        {
            stage.Record(CommandKind.Unrelate, source, type, other: target);
            NoteRecordedEdge(source, target, type);
            return HasRelation<R>(source, target);
        }
        return FindRelations<R>()?.Unrelate(this, source, target, default) == true;
    }

    /// <summary>Moves the edge to <paramref name="newTarget"/>, keeping its payload; inside a query walk the move waits until the walk
    /// closes.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="source"/> has no edge to <paramref name="oldTarget"/>, counting the
    /// edge writes that wait.</exception>
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
        if (!allocator.IsCurrent(source) || !allocator.IsCurrent(oldTarget))
            throw NoEdge<R>(source, oldTarget);
        var type = registry.IndexOf<R>();
        var waits = EdgeWaits(source, oldTarget, type) || TargetWaits(newTarget, type);
        if (
            !(
                waits
                    ? RelatedAsRecorded<R>(source, oldTarget, type)
                    : HasRelation<R>(source, oldTarget)
            )
        )
            throw NoEdge<R>(source, oldTarget);
        if (waits || RecordsEdge(source, oldTarget, type))
        {
            RelatableStore<R>(source, newTarget);
            CheckRelatable(type, source, newTarget, asRecorded: true);
            stage.Record(
                CommandKind.Retarget,
                source,
                type,
                other: newTarget,
                oldTarget: oldTarget
            );
            NoteRecordedEdge(source, oldTarget, type);
            NoteRecordedEdge(source, newTarget, type);
            return;
        }
        if (
            FindRelations<R>() is not { } store
            || store.IndexOf(source, oldTarget) is not (var at and >= 0)
        )
            throw NoEdge<R>(source, oldTarget);
        RelatableStore<R>(source, newTarget);
        var payload = store.EdgesOf(source)[at].Payload;
        store.Retarget(this, source, oldTarget, newTarget, payload);
        VerifyInvariants();
    }

    static InvalidOperationException NoEdge<R>(Entity source, Entity target) =>
        new($"{source} has no {typeof(R).Name} edge to {target}.");

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

    /// <summary>Removes every <typeparamref name="R"/> edge from <paramref name="source"/>; inside a query walk, once the walk closes.</summary>
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
        if (!allocator.IsCurrent(source))
            return;
        var type = registry.IndexOf<R>();
        if (RecordsEdge(source, default, type))
        {
            stage.Record(CommandKind.ClearTargets, source, type);
            NoteRecordedEdge(source, default, type);
            return;
        }
        FindRelations<R>()?.ClearFrom(this, source, default);
    }

    /// <summary>Removes every <typeparamref name="R"/> edge to <paramref name="target"/>; inside a query walk, once the walk closes.</summary>
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
        if (!allocator.IsCurrent(target))
            return;
        var type = registry.IndexOf<R>();
        if (RecordsEdge(default, target, type))
        {
            stage.Record(CommandKind.ClearSources, target, type);
            NoteRecordedSources(target, type);
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
        if (SurvivorOf(root) is { } error)
            throw error;
    }

    TargetPolicyException? SurvivorOf(Entity root)
    {
        BeginDoomed();
        DoomSubtree(root, 1);
        DoomCascades();
        return FindSurvivor();
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

    // With keepDying, a descendant pending destroy itself keeps its own fate, and so does its subtree.
    void DoomSubtree(Entity root, int wave, bool keepDying = false)
    {
        var count = 0;
        Push(ref count, root);
        while (count > 0)
        {
            var entity = walk[--count];
            if (
                doomedWaves[entity.Index] != 0
                || (keepDying && entity != root && OwnsDestroy(allocator.Slots[entity.Index]))
            )
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

    // A cascade releases a wave later and may yet be spared, so only a source of the same or an earlier wave goes first.
    TargetPolicyException? FindSurvivor()
    {
        foreach (var store in relations)
        {
            if (store is not { Policy: TargetPolicy.Throw })
                continue;
            foreach (var target in doomed)
            {
                foreach (var source in store.SourcesOf(target))
                {
                    var wave = doomedWaves[source.Index];
                    if ((wave != 0 && wave <= doomedWaves[target.Index]) || !IsLive(source))
                        continue;
                    EndDoomed();
                    var name = registry[store.Type].Name;
                    return new TargetPolicyException(
                        $"{target} is the target of {name} from {source}, which outlives it; {name} forbids destroying a target with surviving sources."
                    );
                }
            }
        }
        EndDoomed();
        return null;
    }

    void EndDoomed()
    {
        foreach (var entity in doomed)
            doomedWaves[entity.Index] = 0;
        doomed.Clear();
    }

    TargetPolicyException? RevivalFault(Entity root)
    {
        if (!guardsTargets || !AnyThrowEdge())
            return null;
        BeginDoomed();
        DoomSubtree(root, 1, keepDying: true);
        foreach (var store in relations)
        {
            if (store is not { Policy: TargetPolicy.Throw })
                continue;
            foreach (var member in doomed)
            {
                for (var edge = store.EdgeCount(member) - 1; edge >= 0; edge--)
                {
                    var target = store.EdgeTarget(member, edge);
                    if (doomedWaves[target.Index] != 0 || MayLive(target))
                        continue;
                    EndDoomed();
                    var name = registry[store.Type].Name;
                    return new TargetPolicyException(
                        $"{member} cannot leave pending destroy while its {name} edge points at dying {target}; {name} forbids destroying a target with surviving sources."
                    );
                }
            }
        }
        EndDoomed();
        return null;
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
