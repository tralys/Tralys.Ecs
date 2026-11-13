namespace Tralys.Ecs;

/// <summary>What a <see cref="ChangeWatch{TBits}"/> marks when changes cannot be followed one by one: everything when its log was
/// trimmed, and the whole of an entity that comes back from detached, pending destroy or a restore. Static, so it cannot capture state.</summary>
public interface IWatchScope<TBits>
    where TBits : unmanaged, Enum
{
    /// <summary>Marks every entity the consumer may owe work, with what it owes.</summary>
    static abstract void Everything(World world, ChangeSet<TBits> into);

    /// <summary>What <paramref name="entity"/> owes when it comes back; no bits when the consumer does not track it.</summary>
    static abstract TBits Whole(World world, Entity entity);
}
