using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>Random-access reads of <typeparamref name="T"/>. As a system field it declares a read; the generator binds it to the world of each run.</summary>
public readonly struct Lookup<T>(World world)
    where T : struct, IComponent
{
    /// <inheritdoc cref="Tralys.Ecs.World.Has{T}(Entity)"/>
    [Effect(Effects.None)]
    public bool Has(Entity entity) => world.Has<T>(entity);

    /// <inheritdoc cref="Tralys.Ecs.World.Get{T}(Entity)"/>
    [Effect(Effects.None)]
    public ref readonly T Get(Entity entity) => ref world.Get<T>(entity);

    /// <inheritdoc cref="Tralys.Ecs.World.TryGet{T}(Entity, out T)"/>
    [Effect(Effects.None)]
    public bool TryGet(Entity entity, out T value) => world.TryGet(entity, out value);

    /// <inheritdoc cref="Tralys.Ecs.World.Find{T}(Entity)"/>
    [Effect(Effects.None)]
    public Optional<T> Find(Entity entity) => world.Find<T>(entity);
}

/// <summary>Random-access writes of <typeparamref name="T"/>. As a system field it declares a write. A lookup never changes structure: adds
/// and removes go through <see cref="Commands"/>. Writes stamp only on actual change.</summary>
public readonly struct RefLookup<T>(World world)
    where T : struct, IComponent
{
    internal World World => world;

    /// <inheritdoc cref="Tralys.Ecs.World.Has{T}(Entity)"/>
    [Effect(Effects.None)]
    public bool Has(Entity entity) => world.Has<T>(entity);

    /// <inheritdoc cref="Tralys.Ecs.World.Get{T}(Entity)"/>
    [Effect(Effects.None)]
    public ref readonly T Get(Entity entity) => ref world.Get<T>(entity);

    /// <inheritdoc cref="Tralys.Ecs.World.TryGet{T}(Entity, out T)"/>
    [Effect(Effects.None)]
    public bool TryGet(Entity entity, out T value) => world.TryGet(entity, out value);

    /// <inheritdoc cref="Tralys.Ecs.World.Find{T}(Entity)"/>
    [Effect(Effects.None)]
    public Optional<T> Find(Entity entity) => world.Find<T>(entity);

    /// <inheritdoc cref="World.Replace{T}"/>
    [Effect(Effects.Mutates)]
    public void Replace(
        Entity entity,
        in T value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    ) => world.Replace(entity, value, file, line);
}

/// <summary>In-place edits through a <see cref="RefLookup{T}"/> of a mutable component.</summary>
public static class RefLookupExtensions
{
    /// <inheritdoc cref="Tralys.Ecs.World.Edit{T}(Entity)"/>
    [Effect(Effects.Mutates)]
    public static ComponentEdit<T> Edit<T>(this RefLookup<T> lookup, Entity entity)
        where T : struct, IMutableComponent => lookup.World.Edit<T>(entity);

    /// <inheritdoc cref="Tralys.Ecs.World.FindEdit{T}(Entity)"/>
    [Effect(Effects.Mutates)]
    public static ComponentEdit<T> FindEdit<T>(this RefLookup<T> lookup, Entity entity)
        where T : struct, IMutableComponent => lookup.World.FindEdit<T>(entity);
}

/// <summary>Random access through a generated view. As a system field it declares the view's parts.</summary>
public readonly struct ViewLookup<TView>(World world)
    where TView : struct, IEntityView<TView>
{
    /// <summary>Binds the view to the entity; false when it does not exist or lacks a non-optional part.</summary>
    [Effect(Effects.None)]
    public bool TryGet(Entity entity, out TView view) =>
        TView.TryFrom(new WorldEntity(world, entity), out view);
}
