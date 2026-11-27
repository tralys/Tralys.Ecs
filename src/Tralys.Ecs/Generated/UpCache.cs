namespace Tralys.Ecs.Generated;

/// <summary>Finds <see cref="Up{T}"/> for consecutive rows, reusing the last answer while rows share a parent.</summary>
public ref struct UpCache<T>
    where T : struct, IComponent
{
    readonly World world;
    Entity lastParent;
    Entity lastFound;
    int lastRecords;

    /// <summary>An empty cache over the world <paramref name="chunk"/> belongs to; generated query bodies make one per chunk.</summary>
    public UpCache(in ChunkView chunk) => world = chunk.world;

    /// <summary>The nearest strict ancestor of <paramref name="entity"/> holding <typeparamref name="T"/>, or a default <see cref="Up{T}"/>,
    /// whose <c>HasValue</c> is false, when none does.</summary>
    public Up<T> Find(Entity entity)
    {
        var parent = world.hierarchy.ParentOf(entity);
        if (parent.IsDefault)
            return default;
        var records = world.stage.count;
        if (parent == lastParent && records == lastRecords)
        {
            if (lastFound.IsDefault)
                return default;
            var cached = world.Find<T>(lastFound);
            if (cached.HasValue)
                return new Up<T>(lastFound, in cached.Value);
        }
        lastParent = parent;
        lastRecords = records;
        for (var up = parent; !up.IsDefault; up = world.hierarchy.ParentOf(up))
        {
            var found = world.Find<T>(up);
            if (found.HasValue)
            {
                lastFound = up;
                return new Up<T>(up, in found.Value);
            }
        }
        lastFound = default;
        return default;
    }
}
