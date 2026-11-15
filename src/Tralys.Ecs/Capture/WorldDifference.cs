namespace Tralys.Ecs;

/// <summary>What a <see cref="WorldDifference"/> found.</summary>
public enum DifferenceKind : byte
{
    /// <summary>Only the left capture has an entity at this position.</summary>
    EntityOnlyInLeft,

    /// <summary>Only the right capture has an entity at this position.</summary>
    EntityOnlyInRight,

    /// <summary>One side's entity is detached and the other's is not.</summary>
    PartitionDiffers,

    /// <summary>Only the left entity has the component or tag.</summary>
    ComponentOnlyInLeft,

    /// <summary>Only the right entity has the component or tag.</summary>
    ComponentOnlyInRight,

    /// <summary>Both entities have the component, with different values.</summary>
    ValueDiffers,

    /// <summary>The enableable component is enabled on one side only.</summary>
    EnabledDiffers,

    /// <summary>The entities have different parents.</summary>
    ParentDiffers,

    /// <summary>The entities' captured children differ, or come in another order.</summary>
    ChildOrderDiffers,

    /// <summary>The entities' outgoing edges of the relation differ in targets, payloads or order.</summary>
    RelationsDiffer,

    /// <summary>The entities' rows of the type differ in values or order.</summary>
    RowsDiffer,
}

/// <summary>One difference between two captures. <paramref name="Position"/> is the entity's place in capture order, which pairs the two
/// sides; <paramref name="Left"/> and <paramref name="Right"/> are its source handles (null on a side that lacks it), and
/// <paramref name="Type"/> the component, relation or row type, if the difference has one.</summary>
public readonly record struct WorldDifference(
    DifferenceKind Kind,
    int Position,
    Entity Left,
    Entity Right,
    ComponentDescriptor? Type
)
{
    /// <summary><c>#Position (Left / Right): Kind</c>, followed by the type's name when there is one.</summary>
    public override string ToString() =>
        $"#{Position} ({Left} / {Right}): {Kind}{(Type is null ? "" : " " + Type.Name)}";
}
