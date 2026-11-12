namespace Tralys.Ecs.Generated;

/// <summary>Creates the per-world row store of one row type; emitted by the generator because it needs the key type.</summary>
public readonly unsafe struct RowStoreFactory
{
    readonly delegate* <World, int, RowStore> create;

    RowStoreFactory(delegate* <World, int, RowStore> create) => this.create = create;

    /// <summary>The factory for row type <typeparamref name="TRow"/>; generated metadata sets it as <see cref="ComponentInfo.RowFactory"/>.</summary>
    public static RowStoreFactory For<TKey, TRow>()
        where TKey : notnull
        where TRow : struct, IRow<TKey> => new(&Create<TKey, TRow>);

    static RowStore Create<TKey, TRow>(World world, int type)
        where TKey : notnull
        where TRow : struct, IRow<TKey> => new RowStore<TKey, TRow>(world, type);

    internal bool IsSet => create != null;

    internal RowStore Invoke(World world, int type) => create(world, type);
}
