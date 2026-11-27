namespace Tralys.Ecs;

/// <summary>Implemented by generated <see cref="ViewAttribute"/> structs.</summary>
public interface IEntityView<TSelf>
    where TSelf : struct, IEntityView<TSelf>
{
    /// <summary>Binds a view to <paramref name="entity"/>; false when it does not exist or lacks a non-optional part.</summary>
    [Effect(Effects.None)]
    static abstract bool TryFrom(WorldEntity entity, out TSelf view);

    /// <summary>The entity this view reads and writes through.</summary>
    WorldEntity Entity { get; }
}
