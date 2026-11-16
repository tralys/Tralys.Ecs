namespace Tralys.Ecs.Benchmarks;

/// <summary>A minimal sparse set: entity index to dense slot, the baseline single-entity lookups are measured against.</summary>
public sealed class SparseSetProto<T>
    where T : struct
{
    int[] sparse = new int[64];
    T[] dense = new T[64];
    int count;

    public void Add(int entity, in T value)
    {
        if (entity >= sparse.Length)
            Array.Resize(ref sparse, Math.Max(entity + 1, sparse.Length * 2));
        if (count == dense.Length)
            Array.Resize(ref dense, count * 2);
        dense[count] = value;
        sparse[entity] = ++count;
    }

    public bool Has(int entity) => entity < sparse.Length && sparse[entity] != 0;

    public ref T Get(int entity) => ref dense[sparse[entity] - 1];
}
