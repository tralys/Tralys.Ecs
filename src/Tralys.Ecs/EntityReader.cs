using System.Diagnostics.CodeAnalysis;

namespace Tralys.Ecs;

/// <summary>One entity as a <see cref="WorldReader"/> reads it: the read half of <see cref="WorldEntity"/>, for code that writes nothing,
/// such as a parallel query body or a helper both kinds of body call. Reads of an entity that does not exist, including through
/// <c>default(EntityReader)</c>, report absence, and <see cref="Get{T}"/> throws.</summary>
public readonly ref struct EntityReader(WorldReader reader, Entity entity)
{
    readonly World? world = reader.World;

    /// <summary>The handle without its world.</summary>
    public Entity Entity { get; } = entity;

    /// <summary>The reads of the whole world this entity belongs to.</summary>
    public WorldReader Reader => world is null ? default : world.Reader;

    /// <inheritdoc cref="Tralys.Ecs.World.IsLive(Entity)"/>
    public bool IsLive => world is not null && world.IsLive(Entity);

    /// <inheritdoc cref="Tralys.Ecs.World.StateOf(Entity)"/>
    public LifecycleState State => world?.StateOf(Entity) ?? LifecycleState.Dead;

    [MemberNotNullWhen(true, nameof(world))]
    bool Current => world is not null && world.Exists(Entity);

    /// <inheritdoc cref="Tralys.Ecs.World.Has{T}(Entity)"/>
    [Effect(Effects.None)]
    public bool Has<T>()
        where T : struct, IComponent => Current && world.Has<T>(Entity);

    /// <inheritdoc cref="Tralys.Ecs.World.HasStored{T}(Entity)"/>
    [Effect(Effects.None)]
    public bool HasStored<T>()
        where T : struct, IComponent => Current && world.HasStored<T>(Entity);

    /// <inheritdoc cref="Tralys.Ecs.World.Get{T}(Entity)"/>
    [Effect(Effects.None)]
    public ref readonly T Get<T>()
        where T : struct, IComponent => ref (world ?? throw World.NotAlive(Entity)).Get<T>(Entity);

    /// <inheritdoc cref="Tralys.Ecs.World.TryGet{T}(Entity, out T)"/>
    [Effect(Effects.None)]
    public bool TryGet<T>(out T value)
        where T : struct, IComponent
    {
        if (Current)
            return world.TryGet(Entity, out value);
        value = default;
        return false;
    }

    /// <inheritdoc cref="Tralys.Ecs.World.TryGetStored{T}(Entity, out T)"/>
    [Effect(Effects.None)]
    public bool TryGetStored<T>(out T value)
        where T : struct, IComponent
    {
        if (Current)
            return world.TryGetStored(Entity, out value);
        value = default;
        return false;
    }

    /// <inheritdoc cref="Tralys.Ecs.World.Find{T}(Entity)"/>
    [Effect(Effects.None)]
    public Optional<T> Find<T>()
        where T : struct, IComponent => Current ? world.Find<T>(Entity) : default;

    /// <inheritdoc cref="Tralys.Ecs.World.Parent"/>
    [Effect(Effects.None)]
    public EntityReader Parent() => Current ? new(Reader, world.Parent(Entity)) : default;

    /// <inheritdoc cref="Tralys.Ecs.World.Children"/>
    [Effect(Effects.None)]
    public EntityList Children() => Current ? world.Children(Entity) : default;

    /// <summary>The handle without its world.</summary>
    public static implicit operator Entity(EntityReader entity) => entity.Entity;
}
