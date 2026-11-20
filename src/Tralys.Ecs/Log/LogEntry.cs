namespace Tralys.Ecs;

/// <summary>What a structural-log entry records.</summary>
public enum LogKind : byte
{
    /// <summary>An entity was created; one recorded through <see cref="Commands"/> or created by an observer is logged when playback
    /// places it.</summary>
    Created,

    /// <summary>An entity was released by the playback that finalised its destroy; its components are not logged as removed.</summary>
    Destroyed,

    /// <summary>A component or tag was added.</summary>
    Added,

    /// <summary>A <see cref="ReplaceOnlyAttribute"/> component was overwritten with a value whose bytes differ; other overwrites only stamp.</summary>
    Replaced,

    /// <summary>A component or tag was removed; destroying an entity logs no removals.</summary>
    Removed,

    /// <summary>A component's or tag's enable bit was set.</summary>
    Enabled,

    /// <summary>A component's or tag's enable bit was cleared.</summary>
    Disabled,

    /// <summary>The entity moved to the detached partition, which queries skip unless they include detached entities.</summary>
    Detached,

    /// <summary>The entity left the detached partition.</summary>
    Reattached,

    /// <summary>The entity left the world through <see cref="World.Extract"/>; logged instead of <see cref="Destroyed"/>.</summary>
    Extracted,

    /// <summary>An edge was added; <see cref="LogEntry.Entity"/> is the source and <see cref="LogEntry.Other"/> the target.</summary>
    Related,

    /// <summary>An edge was removed.</summary>
    Unrelated,

    /// <summary>An existing edge was related again with a payload whose bytes differ.</summary>
    RelationChanged,

    /// <summary>The entity's parent or its place among its siblings changed; <see cref="LogEntry.Other"/> is the parent now.</summary>
    Reparented,

    /// <summary>A keyed row was inserted; writing to an existing key stamps the row instead.</summary>
    RowAdded,

    /// <summary>A keyed row was removed.</summary>
    RowRemoved,

    /// <summary>An entity was restored from a capture image, after its components, parent, relations and rows.</summary>
    Restored,

    /// <summary>The entity stopped being pending destroy, through <see cref="World.CancelDestroy"/> or by leaving a pending-destroy
    /// ancestor; a cursor that skipped it while pending resyncs it from current state.</summary>
    DestroyCancelled,
}

/// <summary>One structural-log entry: what happened to <see cref="Entity"/>, at which world version, and for which type.</summary>
public readonly record struct LogEntry
{
    internal LogEntry(
        LogKind kind,
        Entity entity,
        ulong version,
        int typeIndex,
        LifecycleReason reason,
        ulong detail
    )
    {
        Kind = kind;
        Entity = entity;
        Version = version;
        TypeIndex = typeIndex;
        Reason = reason;
        Detail = detail;
    }

    /// <summary>What happened.</summary>
    public LogKind Kind { get; }

    /// <summary>The entity the entry is about: the source for a relation entry, the child for <see cref="LogKind.Reparented"/>.</summary>
    public Entity Entity { get; }

    /// <summary>The world version current when the entry was logged.</summary>
    public ulong Version { get; }

    /// <summary>The dense index of the component, relation or row type the entry is for; -1 for an entry about the entity itself.</summary>
    public int TypeIndex { get; }

    /// <summary>The reason the operation was given, or <see cref="LifecycleReason.None"/>.</summary>
    public LifecycleReason Reason { get; }

    internal ulong Detail { get; }

    /// <summary>For <see cref="LogKind.Related"/>, <see cref="LogKind.Unrelated"/> and <see cref="LogKind.RelationChanged"/>, the target;
    /// for <see cref="LogKind.Reparented"/>, the new parent (null when detached); null for every other kind.</summary>
    public Entity Other =>
        Kind
            is LogKind.Related
                or LogKind.Unrelated
                or LogKind.RelationChanged
                or LogKind.Reparented
            ? Entity.FromBits(Detail)
            : default;

    /// <summary>True when the entry is for type <typeparamref name="T"/>; false for an entry about the entity itself.</summary>
    public bool Is<T>()
        where T : struct, IDeclared => TypeIndex >= 0 && TypeIndex == ComponentType<T>.Index;
}

/// <summary>A set of <see cref="ComponentCategoryAttribute"/> bits; a filter passes a type in any of them, and the empty mask passes every type.</summary>
/// <param name="Bits">One bit per category, numbered as <see cref="ComponentCategoryAttribute.Bit"/>.</param>
public readonly record struct CategoryMask(ulong Bits)
{
    /// <summary>The empty mask, which passes every type.</summary>
    public static CategoryMask All => default;

    /// <summary>The mask with each of <paramref name="bits"/> set; bits run 0 to 63.</summary>
    public static CategoryMask Of(params ReadOnlySpan<int> bits)
    {
        ulong mask = 0;
        foreach (var bit in bits)
            mask |= 1UL << bit;
        return new CategoryMask(mask);
    }

    /// <summary>True when <paramref name="categories"/> shares a bit with this mask, or this mask is empty.</summary>
    public bool Matches(ulong categories) => Bits == 0 || (Bits & categories) != 0;
}
