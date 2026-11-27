namespace Tralys.Ecs.Generated;

/// <summary>Creates a world's store for one index declaration; emitted by the generator because it needs the key type.</summary>
public readonly unsafe struct IndexFactory
{
    [Effect(Effects.None)]
    readonly delegate* <World, int, IndexDeclaration, IndexStore> create;

    IndexFactory(delegate* <World, int, IndexDeclaration, IndexStore> create) =>
        this.create = create;

    /// <summary>A factory for a <see cref="IndexKind.Unique"/> index keyed by <typeparamref name="TKeyer"/>.</summary>
    public static IndexFactory Unique<TComponent, TKey, TKeyer>()
        where TComponent : struct
        where TKey : notnull
        where TKeyer : IIndexKey<TComponent, TKey> => new(&CreateUnique<TComponent, TKey, TKeyer>);

    /// <summary>A factory for a <see cref="IndexKind.Multi"/> index keyed by <typeparamref name="TKeyer"/>.</summary>
    public static IndexFactory Multi<TComponent, TKey, TKeyer>()
        where TComponent : struct
        where TKey : notnull
        where TKeyer : IIndexKey<TComponent, TKey> => new(&CreateMulti<TComponent, TKey, TKeyer>);

    /// <summary>A factory for an <see cref="IndexKind.Ordered"/> index keyed by <typeparamref name="TKeyer"/>.</summary>
    public static IndexFactory Ordered<TComponent, TKey, TKeyer>()
        where TComponent : struct
        where TKey : notnull
        where TKeyer : IIndexKey<TComponent, TKey> => new(&CreateOrdered<TComponent, TKey, TKeyer>);

    static IndexStore CreateUnique<TComponent, TKey, TKeyer>(
        World world,
        int type,
        IndexDeclaration declaration
    )
        where TComponent : struct
        where TKey : notnull
        where TKeyer : IIndexKey<TComponent, TKey> =>
        new UniqueStore<TKey>(
            world,
            type,
            declaration,
            new KeyReader<TComponent, TKey, TKeyer>(declaration.PerParent)
        );

    static IndexStore CreateMulti<TComponent, TKey, TKeyer>(
        World world,
        int type,
        IndexDeclaration declaration
    )
        where TComponent : struct
        where TKey : notnull
        where TKeyer : IIndexKey<TComponent, TKey> =>
        new MultiStore<TKey>(
            world,
            type,
            declaration,
            new KeyReader<TComponent, TKey, TKeyer>(declaration.PerParent)
        );

    static IndexStore CreateOrdered<TComponent, TKey, TKeyer>(
        World world,
        int type,
        IndexDeclaration declaration
    )
        where TComponent : struct
        where TKey : notnull
        where TKeyer : IIndexKey<TComponent, TKey> =>
        new OrderedStore<TKey>(
            world,
            type,
            declaration,
            new KeyReader<TComponent, TKey, TKeyer>(declaration.PerParent)
        );

    internal IndexStore Invoke(World world, int type, IndexDeclaration declaration) =>
        create(world, type, declaration);
}
