namespace Tralys.Ecs;

/// <summary>What <see cref="EntityWatch{T}.Poll"/> saw since the previous poll.</summary>
public enum WatchResult
{
    /// <summary>The component is present and not stamped since the last poll; always so for an untracked component.</summary>
    Unchanged,

    /// <summary>The component was added or changed since the last poll.</summary>
    Changed,

    /// <summary>The entity exists but lacks the component or holds it disabled.</summary>
    Removed,

    /// <summary>The handle no longer names an entity.</summary>
    EntityGone,
}

/// <summary>Watches one entity's component. Keyed by the full handle, so a recycled slot reads as <see cref="WatchResult.EntityGone"/>.</summary>
public struct EntityWatch<T>(Entity entity)
    where T : struct, IComponent
{
    ulong seen;

    /// <summary>The watched handle.</summary>
    public readonly Entity Entity => entity;

    /// <summary>Compares the component's change stamp with the one the previous poll saw and remembers it, so poll a mutable
    /// location, not a copy.</summary>
    public WatchResult Poll(World world)
    {
        if (!world.Exists(entity))
            return WatchResult.EntityGone;
        if (!world.Has<T>(entity))
        {
            seen = 0;
            return WatchResult.Removed;
        }
        var stamp = world.changes.VersionOf<T>(entity);
        if (stamp > seen)
        {
            seen = stamp;
            return WatchResult.Changed;
        }
        return WatchResult.Unchanged;
    }
}
