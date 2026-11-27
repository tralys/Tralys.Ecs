namespace Tralys.Ecs;

internal sealed class BrokenInvariantException(string detail)
    : InvalidOperationException($"World invariant broken: {detail}");

public sealed partial class World
{
    readonly bool verifyInvariants;

    void VerifyInvariants()
    {
        if (verifyInvariants && !playing && BrokenInvariant() is { } broken)
            throw broken;
    }

    BrokenInvariantException? BrokenInvariant()
    {
        var slots = allocator.Slots;
        for (var index = 1u; index < (uint)allocator.Capacity; index++)
        {
            ref readonly var slot = ref slots[index];
            if (slot.State == LifecycleState.Dead)
                continue;
            var entity = new Entity(index, slot.Generation);
            var parent = hierarchy.ParentOf(entity);
            if (!parent.IsDefault && !allocator.IsCurrent(parent))
                return Broken($"{entity}'s parent {parent} was released.");
            if (slot.Archetype is not { } archetype)
            {
                if (slot.State == LifecycleState.PendingDestroy)
                    return Broken($"{entity} is pending destroy without a row.");
                continue;
            }
            if (archetype.Entities[slot.Row] != entity)
                return Broken($"{entity}'s row holds {archetype.Entities[slot.Row]}.");
            ref readonly var above = ref slots[parent.Index];
            var detached = slot.DetachedSelf || (!parent.IsDefault && above.Detached);
            if (slot.Detached != detached || archetype.Detached != detached)
                return Broken(
                    $"{entity} is {(archetype.Detached ? "" : "not ")}in the detached partition, yet its own flag and its parent's put it {(detached ? "in" : "out of")} it."
                );
            var pending = slot.State == LifecycleState.PendingDestroy;
            if (archetype.IsPendingDestroy(slot.Row) != pending)
                return Broken(
                    $"{entity}'s pending-destroy bit disagrees with its state {slot.State}."
                );
            if (pending && !slot.DestroyQueued)
                return Broken($"{entity} is pending destroy but not queued for finalisation.");
            if (archetype.IsPending(slot.Row) != slot.Pending)
                return Broken($"{entity}'s row pending bit disagrees with its slot.");
            var inherited =
                !parent.IsDefault
                && (
                    above.State == LifecycleState.PendingDestroy
                    || (above.Archetype?.HasAncestor(0, above.Row) ?? false)
                );
            if (archetype.HasAncestor(0, slot.Row) != inherited)
                return Broken(
                    $"{entity}'s inherited pending-destroy bit disagrees with its parent {parent}."
                );
        }
        foreach (var store in relations)
        {
            if (store is null)
                continue;
            for (var node = 0; node < store.TargetNodes; node++)
            {
                var target = store.TargetNode(node);
                if (target.IsDefault)
                    continue;
                var sources = store.SourcesOf(target);
                if (sources.IsEmpty)
                    continue;
                if (!allocator.IsCurrent(target))
                    return Broken(
                        $"{sources[0]} holds a {registry[store.Type].Name} edge onto released {target}."
                    );
                if (store.Policy != TargetPolicy.Throw || IsLive(target))
                    continue;
                foreach (var source in sources)
                {
                    if (IsLive(source))
                        return Broken(
                            $"Live {source} holds a {registry[store.Type].Name} edge onto dying {target}, which forbids it."
                        );
                }
            }
        }
        return null;
    }

    void VerifyReleased(List<Entity> plan)
    {
        if (!verifyInvariants)
            return;
        foreach (var member in plan)
        {
            if (allocator.IsCurrent(member))
                throw Broken($"{member} was planned for release but survived its finalisation.");
        }
    }

    static BrokenInvariantException Broken(string detail) => new(detail);
}
