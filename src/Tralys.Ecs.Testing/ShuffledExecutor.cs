namespace Tralys.Ecs;

/// <summary>A test executor: runs items on the calling thread in a seeded random order, each on a random worker, to expose results that
/// depend on chunk-to-worker assignment.</summary>
public sealed class ShuffledExecutor(int seed, int workers = 4) : IChunkExecutor
{
    /// <inheritdoc/>
    public int WorkerCount { get; } =
        workers > 0 ? workers : throw new ArgumentOutOfRangeException(nameof(workers));

    /// <inheritdoc/>
    public void Run<TJob>(ref TJob job, int itemCount)
        where TJob : struct, IPartitionJob
    {
        var random = new Random(seed);
        var order = new int[itemCount];
        for (var i = 0; i < itemCount; i++)
            order[i] = i;
        random.Shuffle(order);
        foreach (var item in order)
            job.Execute(item, random.Next(WorkerCount));
    }
}
