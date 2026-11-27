namespace Tralys.Ecs.Generated;

/// <summary>What a runtime member may do, as the generator's analyzers read it. A member's effects are the union over every path it can
/// take, whatever the arguments.</summary>
[Flags]
public enum Effects
{
    /// <summary>None of the effects below.</summary>
    None = 0,

    /// <summary>Changes structure, directly or through recorded commands: adds or removes components, creates, destroys, detaches or
    /// reparents entities, or plays back.</summary>
    Structural = 1 << 2,

    /// <summary>Can move or reallocate entities' component storage, archetype rows and sparse values, so a borrow of a component taken
    /// before it may read another entity after it.</summary>
    MovesEntities = 1 << 3,

    /// <summary>Changes world state a partitioned query body may not touch.</summary>
    Mutates = 1 << 4,

    /// <summary>Needs a settled world: no query may be iterating.</summary>
    Settled = 1 << 5,

    /// <summary>Uses world scratch, which belongs to the owner thread, so a partitioned query body cannot call it.</summary>
    OwnerThread = 1 << 7,

    /// <summary>Can move or reallocate an entity's keyed rows, so a borrow of a row taken before it may read another row after it.</summary>
    MovesRowBlocks = 1 << 8,
}

/// <summary>Declares a runtime member's <see cref="Effects"/>. On a public method the analyzer checks it equals what the body can reach;
/// on a hidden method or a field it is a primitive, the effect of calling the method or writing the field; on an interface, abstract or
/// function-pointer member it bounds what any implementation may do.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Field, Inherited = false)]
public sealed class EffectAttribute(Effects effects) : Attribute
{
    /// <summary>The member's effects, combined as flags.</summary>
    public Effects Effects { get; } = effects;
}

/// <summary>Marks a type whose instances point into world storage, so the analyzer treats a local holding one as a borrow that a later
/// move of that storage invalidates.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class BorrowAttribute : Attribute;
