namespace Tralys.Ecs.Generated;

/// <summary>What a runtime member does, as the generator's analyzers read it.</summary>
[Flags]
public enum Effects
{
    /// <summary>None of the effects below.</summary>
    None = 0,

    /// <summary>Changes structure, directly or through recorded commands: adds or removes components, creates, destroys, detaches or
    /// reparents entities, or plays back.</summary>
    Structural = 1 << 2,

    /// <summary>Can move or reallocate component rows, so a <c>ref</c> taken before it may read another entity after it.</summary>
    MovesRows = 1 << 3,

    /// <summary>Changes world state a partitioned query body may not touch.</summary>
    Mutates = 1 << 4,

    /// <summary>Needs a settled world: no query may be iterating.</summary>
    Settled = 1 << 5,

    /// <summary>Returns a <c>ref</c> or <see cref="Optional{T}"/> into storage that <see cref="MovesRows"/> can move.</summary>
    Borrows = 1 << 6,

    /// <summary>Uses world scratch, which belongs to the owner thread, so a partitioned query body cannot call it.</summary>
    OwnerThread = 1 << 7,
}

/// <summary>Declares a runtime member's <see cref="Effects"/>.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class EffectAttribute(Effects effects) : Attribute
{
    /// <summary>The member's effects, combined as flags.</summary>
    public Effects Effects { get; } = effects;
}
