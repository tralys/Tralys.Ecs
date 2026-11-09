using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>A storable (world, entity) pair forwarding the single-entity API. Reads of an entity that does not exist, including through
/// <c>default(WorldEntity)</c>, report absence; <see cref="Remove{T}"/> and <see cref="Destroy"/> do nothing; every other write throws.</summary>
public readonly struct WorldEntity(World world, Entity entity) : IEquatable<WorldEntity>
{
    /// <summary>The world the entity belongs to; null in <c>default(WorldEntity)</c>.</summary>
    public World World { get; } = world;

    /// <summary>The handle without its world.</summary>
    public Entity Entity { get; } = entity;

    /// <inheritdoc cref="Tralys.Ecs.World.IsLive(Entity)"/>
    public bool IsLive => World is not null && World.IsLive(Entity);

    /// <inheritdoc cref="Tralys.Ecs.World.StateOf(Entity)"/>
    public LifecycleState State => World?.StateOf(Entity) ?? LifecycleState.Dead;

    World Bound => World ?? throw World.NotAlive(Entity);

    bool Current => World is not null && World.Exists(Entity);

    /// <inheritdoc cref="Tralys.Ecs.World.Has{T}(Entity)"/>
    [Effect(Effects.None)]
    public bool Has<T>()
        where T : struct, IComponent => Current && World.Has<T>(Entity);

    /// <inheritdoc cref="Tralys.Ecs.World.HasStored{T}(Entity)"/>
    [Effect(Effects.None)]
    public bool HasStored<T>()
        where T : struct, IComponent => Current && World.HasStored<T>(Entity);

    /// <inheritdoc cref="Tralys.Ecs.World.Get{T}(Entity)"/>
    [Effect(Effects.Borrows)]
    public ref readonly T Get<T>()
        where T : struct, IComponent => ref Bound.Get<T>(Entity);

    /// <inheritdoc cref="Tralys.Ecs.World.TryGet{T}(Entity, out T)"/>
    [Effect(Effects.None)]
    public bool TryGet<T>(out T value)
        where T : struct, IComponent
    {
        if (Current)
            return World.TryGet(Entity, out value);
        value = default;
        return false;
    }

    /// <inheritdoc cref="Tralys.Ecs.World.TryGetStored{T}(Entity, out T)"/>
    [Effect(Effects.None)]
    public bool TryGetStored<T>(out T value)
        where T : struct, IComponent
    {
        if (Current)
            return World.TryGetStored(Entity, out value);
        value = default;
        return false;
    }

    /// <inheritdoc cref="Tralys.Ecs.World.Find{T}(Entity)"/>
    [Effect(Effects.Borrows)]
    public Optional<T> Find<T>()
        where T : struct, IComponent => Current ? World.Find<T>(Entity) : default;

    /// <summary>A scoped <c>ref</c> to the present component, written in place at once; disposing it stamps and reindexes only if the
    /// value changed.</summary>
    /// <exception cref="InvalidOperationException">The entity lacks <typeparamref name="T"/>, holds it disabled, or does not
    /// exist.</exception>
    [Effect(Effects.Mutates)]
    public ComponentEdit<T> Edit<T>()
        where T : struct, IMutableComponent => Bound.Edit<T>(Entity);

    /// <summary>As <see cref="Edit{T}"/>, or an edit without a value when the entity lacks <typeparamref name="T"/>, holds it disabled,
    /// or does not exist.</summary>
    [Effect(Effects.Mutates)]
    public ComponentEdit<T> FindEdit<T>()
        where T : struct, IMutableComponent => Current ? World.FindEdit<T>(Entity) : default;

    /// <summary>Adds or overwrites, enabling a disabled one. An overwrite applies at once and stamps only if the value differs; an add or
    /// enable throws inside a serial query body, and inside an observer waits for the next playback while reads see it at once.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist, or the add breaks a requirement, a singleton claim or a
    /// unique index.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public void Set<T>(
        in T value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent => Bound.Set(Entity, value, file: file, line: line);

    /// <inheritdoc cref="World.Replace{T}"/>
    [Effect(Effects.Mutates)]
    public void Replace<T>(
        in T value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent => Bound.Replace(Entity, value, file, line);

    /// <inheritdoc cref="Tralys.Ecs.World.Add{T}"/>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public void Add<T>([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        where T : struct, ITag => Bound.Add<T>(Entity, file: file, line: line);

    /// <summary>An edit of the component, first set to <paramref name="initial"/>, and so enabled, when missing or disabled; an add or
    /// enable throws inside a serial query body, and inside an observer waits for the next playback while the edit writes the pending
    /// value.</summary>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public ComponentEdit<T> EditOrAdd<T>(
        in T initial = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IMutableComponent =>
        Bound.EditOrAdd(Entity, initial, file: file, line: line);

    /// <summary><see cref="Set{T}"/> when <paramref name="value"/> has a value, otherwise <see cref="Remove{T}"/>.</summary>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public void SetOrRemove<T>(
        in T? value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent => Bound.SetOrRemove(Entity, value, file: file, line: line);

    /// <inheritdoc cref="Tralys.Ecs.World.Remove{T}"/>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public bool Remove<T>([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        where T : struct, IComponent => Current && World.Remove<T>(Entity, file: file, line: line);

    /// <inheritdoc cref="Tralys.Ecs.World.SetEnabled{T}"/>
    [Effect(Effects.Structural | Effects.Mutates)]
    public void SetEnabled<T>(
        bool enabled,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IEnableable =>
        Bound.SetEnabled<T>(Entity, enabled, file: file, line: line);

    /// <inheritdoc cref="Tralys.Ecs.World.Destroy"/>
    [Effect(Effects.Structural | Effects.Mutates)]
    public void Destroy(
        LifecycleReason reason = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    ) => World?.Destroy(Entity, reason, file: file, line: line);

    /// <inheritdoc cref="Tralys.Ecs.World.Parent"/>
    [Effect(Effects.None)]
    public WorldEntity Parent() => Current ? new(World, World.Parent(Entity)) : default;

    /// <inheritdoc cref="Tralys.Ecs.World.Children"/>
    [Effect(Effects.None)]
    public EntityList Children() => Current ? World.Children(Entity) : default;

    /// <inheritdoc cref="Tralys.Ecs.World.SetParent"/>
    [Effect(Effects.Structural | Effects.Mutates)]
    public void SetParent(
        Entity parent,
        int index = -1,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    ) => Bound.SetParent(Entity, parent, index, file: file, line: line);

    /// <inheritdoc cref="Tralys.Ecs.World.ClearParent"/>
    [Effect(Effects.Structural | Effects.Mutates)]
    public void ClearParent([CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Bound.ClearParent(Entity, file: file, line: line);

    /// <summary>Binds the view <typeparamref name="TView"/> to this entity; false when it does not exist or lacks a non-optional
    /// part.</summary>
    [Effect(Effects.None)]
    public bool TryAs<TView>(out TView view)
        where TView : struct, IEntityView<TView> => TView.TryFrom(this, out view);

    /// <summary>The same world instance and the same handle.</summary>
    public bool Equals(WorldEntity other) =>
        ReferenceEquals(World, other.World) && Entity == other.Entity;

    /// <summary>Compares the handle alone, as <c>==</c> does through the implicit conversion to <see cref="Tralys.Ecs.Entity"/>.</summary>
    public bool Equals(Entity other) => Entity == other;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is WorldEntity other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(RuntimeHelpers.GetHashCode(World), Entity);

    /// <summary>The same world instance and the same handle.</summary>
    public static bool operator ==(WorldEntity left, WorldEntity right) => left.Equals(right);

    /// <summary>A different world instance or a different handle.</summary>
    public static bool operator !=(WorldEntity left, WorldEntity right) => !left.Equals(right);

    /// <summary>The handle without its world, so a <see cref="WorldEntity"/> or a view's <c>Entity</c> passes wherever an
    /// <see cref="Tralys.Ecs.Entity"/> is taken.</summary>
    public static implicit operator Entity(WorldEntity entity) => entity.Entity;
}
