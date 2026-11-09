namespace Tralys.Ecs;

/// <summary>Where an entity is in its lifecycle, as <see cref="World.StateOf"/> reports it.</summary>
public enum LifecycleState : byte
{
    /// <summary>The handle names no entity: never issued, or finalised.</summary>
    Dead,

    /// <summary>Created by an observer: usable by handle, invisible to queries until the next playback.</summary>
    Reserved,

    /// <summary>Placed in storage; matched by queries unless detached.</summary>
    Alive,

    /// <summary>Destroyed but still readable by handle until the next playback that finalises destroys.</summary>
    PendingDestroy,
}

/// <summary>Why a lifecycle change happened. Codes below 256 are reserved for the ECS; layers use 256 and up.</summary>
public readonly record struct LifecycleReason(ushort Code)
{
    /// <summary>No reason given.</summary>
    public static LifecycleReason None => default;

    /// <summary>A restore or reset replaced the world's data.</summary>
    public static LifecycleReason Restored => new(1);

    /// <summary>Destroyed with a destroyed ancestor, or as the source of a <see cref="TargetPolicy.DestroySource"/> relation whose target
    /// was destroyed.</summary>
    public static LifecycleReason Cascade => new(2);
}
