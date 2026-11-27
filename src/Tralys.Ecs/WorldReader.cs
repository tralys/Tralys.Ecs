namespace Tralys.Ecs;

/// <summary>The reads a query body may make of the world it runs on, and the only view of it a parallel body gets: components, lifecycle,
/// singletons, hierarchy, relations and rows. It writes nothing and uses no owner-thread scratch; a parallel body records changes through
/// its <see cref="Commands"/>. <c>default(WorldReader)</c> reads as an empty world: no entity exists, and <see cref="Get{T}"/>,
/// <see cref="GetSingleton{T}"/> and <see cref="IsEffective{T}"/> throw.</summary>
public readonly ref struct WorldReader
{
    readonly World? world;

    internal WorldReader(World world) => this.world = world;

    internal World? World => world;

    /// <inheritdoc cref="World.Has{T}(Entity)"/>
    [Effect(Effects.None)]
    public bool Has<T>(Entity entity)
        where T : struct, IComponent => world is not null && world.Has<T>(entity);

    /// <inheritdoc cref="World.HasStored{T}(Entity)"/>
    [Effect(Effects.None)]
    public bool HasStored<T>(Entity entity)
        where T : struct, IComponent => world is not null && world.HasStored<T>(entity);

    /// <inheritdoc cref="World.Get{T}(Entity)"/>
    [Effect(Effects.None)]
    public ref readonly T Get<T>(Entity entity)
        where T : struct, IComponent => ref (world ?? throw World.NotAlive(entity)).Get<T>(entity);

    /// <inheritdoc cref="World.TryGet{T}(Entity, out T)"/>
    [Effect(Effects.None)]
    public bool TryGet<T>(Entity entity, out T value)
        where T : struct, IComponent
    {
        if (world is not null)
            return world.TryGet(entity, out value);
        value = default;
        return false;
    }

    /// <inheritdoc cref="World.TryGetStored{T}(Entity, out T)"/>
    [Effect(Effects.None)]
    public bool TryGetStored<T>(Entity entity, out T value)
        where T : struct, IComponent
    {
        if (world is not null)
            return world.TryGetStored(entity, out value);
        value = default;
        return false;
    }

    /// <inheritdoc cref="World.Find{T}(Entity)"/>
    [Effect(Effects.None)]
    public Optional<T> Find<T>(Entity entity)
        where T : struct, IComponent => world is null ? default : world.Find<T>(entity);

    /// <inheritdoc cref="World.Exists(Entity)"/>
    [Effect(Effects.None)]
    public bool Exists(Entity entity) => world is not null && world.Exists(entity);

    /// <inheritdoc cref="World.IsLive(Entity)"/>
    [Effect(Effects.None)]
    public bool IsLive(Entity entity) => world is not null && world.IsLive(entity);

    /// <inheritdoc cref="World.StateOf(Entity)"/>
    [Effect(Effects.None)]
    public LifecycleState StateOf(Entity entity) => world?.StateOf(entity) ?? LifecycleState.Dead;

    /// <inheritdoc cref="World.IsDetached(Entity)"/>
    [Effect(Effects.None)]
    public bool IsDetached(Entity entity) => world is not null && world.IsDetached(entity);

    /// <inheritdoc cref="World.HasSingleton{T}"/>
    [Effect(Effects.None)]
    public bool HasSingleton<T>()
        where T : struct, ISingleton => world is not null && world.HasSingleton<T>();

    /// <inheritdoc cref="World.SingletonEntity{T}"/>
    [Effect(Effects.None)]
    public Entity SingletonEntity<T>()
        where T : struct, ISingleton => world is null ? default : world.SingletonEntity<T>();

    /// <inheritdoc cref="World.GetSingleton{T}"/>
    [Effect(Effects.None)]
    public ref readonly T GetSingleton<T>()
        where T : struct, ISingleton =>
        ref (world ?? throw World.NoSingleton<T>()).GetSingleton<T>();

    /// <inheritdoc cref="World.TryGetSingleton{T}(out T)"/>
    [Effect(Effects.None)]
    public bool TryGetSingleton<T>(out T value)
        where T : struct, ISingleton
    {
        if (world is not null)
            return world.TryGetSingleton(out value);
        value = default;
        return false;
    }

    /// <inheritdoc cref="World.Parent(Entity)"/>
    [Effect(Effects.None)]
    public Entity Parent(Entity entity) => world is null ? default : world.Parent(entity);

    /// <inheritdoc cref="World.Children(Entity)"/>
    [Effect(Effects.None)]
    public EntityList Children(Entity entity) => world is null ? default : world.Children(entity);

    /// <inheritdoc cref="World.Depth(Entity)"/>
    [Effect(Effects.None)]
    public int Depth(Entity entity) => world?.Depth(entity) ?? 0;

    /// <inheritdoc cref="World.IsEffective{T}(Entity)"/>
    [Effect(Effects.None)]
    public bool IsEffective<T>(Entity entity)
        where T : struct, ITag, IEnableable =>
        (world ?? throw World.NotAlive(entity)).IsEffective<T>(entity);

    /// <inheritdoc cref="World.HasRelation{R}(Entity, Entity)"/>
    [Effect(Effects.None)]
    public bool HasRelation<R>(Entity source, Entity target)
        where R : struct, IRelation => world is not null && world.HasRelation<R>(source, target);

    /// <inheritdoc cref="World.TryGetRelation{R}(Entity, Entity, out R)"/>
    [Effect(Effects.None)]
    public bool TryGetRelation<R>(Entity source, Entity target, out R payload)
        where R : struct, IRelation
    {
        if (world is not null)
            return world.TryGetRelation(source, target, out payload);
        payload = default;
        return false;
    }

    /// <inheritdoc cref="World.Targets{R}(Entity)"/>
    [Effect(Effects.None)]
    public RelationTargets<R> Targets<R>(Entity source)
        where R : struct, IRelation => world is null ? default : world.Targets<R>(source);

    /// <inheritdoc cref="World.Sources{R}(Entity)"/>
    [Effect(Effects.None)]
    public EntityList Sources<R>(Entity target)
        where R : struct, IRelation => world is null ? default : world.Sources<R>(target);

    /// <summary>The keyed rows of <typeparamref name="TRow"/> on <paramref name="entity"/>, read-only; reading never creates storage.</summary>
    [Effect(Effects.None)]
    public RowsReader<TKey, TRow> Rows<TKey, TRow>(Entity entity)
        where TKey : notnull
        where TRow : struct, IRow<TKey> => world is null ? default : new(world, entity);

    /// <inheritdoc cref="World.RowOwners{TKey, TRow}(in TKey)"/>
    [Effect(Effects.None)]
    public EntityList RowOwners<TKey, TRow>(in TKey key)
        where TKey : notnull
        where TRow : struct, IRow<TKey> =>
        world is null ? default : world.RowOwners<TKey, TRow>(key);
}
