namespace Tralys.Ecs.Benchmarks;

/// <summary>A minimal fixed-capacity chunk store for one component, the baseline iteration and churn are measured against.</summary>
public sealed class ChunkStoreProto<T>(int capacity)
    where T : struct
{
    readonly List<T[]> chunks = [];
    int count;

    public int Count => count;

    public void Add(in T value)
    {
        if (count % capacity == 0)
            chunks.Add(new T[capacity]);
        chunks[count / capacity][count % capacity] = value;
        count++;
    }

    public void RemoveAt(int index)
    {
        var last = count - 1;
        chunks[index / capacity][index % capacity] = chunks[last / capacity][last % capacity];
        count--;
        if (count % capacity == 0)
            chunks.RemoveAt(chunks.Count - 1);
    }

    public IEnumerable<ArraySegment<T>> Chunks()
    {
        for (var c = 0; c < chunks.Count; c++)
            yield return new ArraySegment<T>(
                chunks[c],
                0,
                Math.Min(capacity, count - (c * capacity))
            );
    }
}
