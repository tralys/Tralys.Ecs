using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>A scoped <c>ref</c> to one component. Disposing it stamps the row and moves its index entries only if the value changed; a
/// managed component, whose inner mutations a shallow compare cannot see, counts as changed on every dispose.</summary>
[MustDispose("its change stamp and index update are lost")]
[Borrow]
public ref struct ComponentEdit<T>
    where T : struct, IMutableComponent
{
    World? world;
    readonly ref T value;
    readonly T original;
    readonly ValueLocation location;
    readonly Entity entity;
    readonly bool track;
    readonly bool indexed;
    readonly int structural;
    readonly int sparseMoves;
    readonly PayloadStore? payloads;
    readonly int payloadResets;
    readonly int indexRefreshes;
    readonly bool owns;

    internal ComponentEdit(
        World world,
        ref T value,
        scoped in ValueLocation location,
        Entity entity
    )
    {
        this.world = world;
        this.value = ref value;
        this.location = location;
        this.entity = entity;
        track = location.IsTracked || location.Kind == LocationKind.Overlay;
        indexed = world.hasIndexes && world.layout.IndexBase[ComponentType<T>.Index] >= 0;
        owns = world.layout.OwnsBuffers[ComponentType<T>.Index];
        original =
            (track || indexed || owns) && !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
                ? value
                : default;
        structural = world.structuralCount;
        sparseMoves = location.Sparse?.Moves ?? 0;
        if (location.Kind == LocationKind.Overlay)
        {
            payloads = world.stage.Payloads<T>(ComponentType<T>.Index);
            payloadResets = payloads.Resets;
        }
        indexRefreshes = world.indexRefreshes;
    }

    /// <summary>The component by <c>ref</c>; writes land at once.</summary>
    /// <exception cref="InvalidOperationException">The edit has no value, or a structural change ran since it was opened.</exception>
    public readonly ref T Value
    {
        get
        {
            if (Unsafe.IsNullRef(ref value))
                Optional<T>.ThrowEmpty();
            CheckMoves();
            return ref value;
        }
    }

    readonly void CheckMoves()
    {
        if (
            world is not null
            && (
                world.structuralCount != structural
                || (location.Sparse is { } storage && storage.Moves != sparseMoves)
                || (payloads is not null && payloads.Resets != payloadResets)
            )
        )
            throw new InvalidOperationException(
                "A structural change ran while this edit was open, so its ref may point at another entity's row."
            );
    }

    /// <summary>False for an edit of a component the entity lacks or holds disabled, from <c>FindEdit</c>, or for <c>default</c>.</summary>
    public readonly bool HasValue => !Unsafe.IsNullRef(ref value);

    /// <summary>Stamps the row, unless an untracked scope is open now, and moves its index entries if the value changed; a second dispose
    /// does nothing.</summary>
    /// <exception cref="InvalidOperationException">A structural change ran while the edit was open, or the new value collides in a
    /// unique index.</exception>
    public void Dispose()
    {
        if (world is null)
            return;
        CheckMoves();
        var target = world;
        world = null;
        if (!track && !indexed && !owns)
            return;
        var copy = original;
        var changed = !ComponentOpsOf<T>.Equals(
            ref Unsafe.As<T, byte>(ref copy),
            ref Unsafe.As<T, byte>(ref value)
        );
        if (owns && changed)
            target.ReleaseBuffers(ComponentType<T>.Index, in copy, in value, parallel: false);
        var refreshedMeanwhile = indexed && target.indexRefreshes != indexRefreshes;
        if (!changed && !refreshedMeanwhile)
            return;
        if (track && changed)
        {
            if (location.Kind == LocationKind.Overlay)
                target.EditedRecorded(entity, ComponentType<T>.Index);
            else
                target.Stamp(location);
        }
        if (indexed)
        {
            if (refreshedMeanwhile)
                target.RefreshIndexes(entity, ComponentType<T>.Index);
            else
                target.RefreshChangedIndexes(entity, ComponentType<T>.Index, in copy, in value);
            target.ThrowIndexConflicts();
        }
    }
}
