namespace Tralys.Ecs;

/// <summary>Implemented by the generator on <see cref="BundleAttribute"/> structs.</summary>
public interface IBundle
{
    /// <summary>Adds the type of each of the bundle's fields to <paramref name="shape"/>.</summary>
    [Effect(Effects.None)]
    static abstract void Describe(ref BundleShape shape);

    /// <summary>Hands each non-tag field's value to <paramref name="writer"/>.</summary>
    [Effect(Effects.None)]
    void Write(ref BundleWriter writer);
}

internal static class BundleSlot<TBundle>
    where TBundle : struct, IBundle
{
    public static readonly int Id = BundleIds.Next();
}

internal static class BundleIds
{
    static int next;

    public static int Next() => Interlocked.Increment(ref next) - 1;
}

internal sealed class BundleInfo(
    int[] signatureTypes,
    int[] sparseTags,
    int[] singletons,
    int[] indexedTypes
)
{
    public readonly int[] SignatureTypes = signatureTypes;
    public readonly int[] SparseTags = sparseTags;
    public readonly int[] Singletons = singletons;
    public readonly int[] IndexedTypes = indexedTypes;
    public Archetype? Archetype;
}

// Computed without touching any world, so parallel bodies may read it.
internal static class BundleTags<TBundle>
    where TBundle : struct, IBundle
{
    static int[]? tags;

    public static int[] Of(Registry registry)
    {
        if (tags is { } known)
            return known;
        var types = new List<int>();
        var shape = new BundleShape(types);
        TBundle.Describe(ref shape);
        return tags = [
            .. types.Where(t =>
                registry.Layout.Class[t] is StorageClass.SignatureTag or StorageClass.SparseTag
            ),
        ];
    }
}
