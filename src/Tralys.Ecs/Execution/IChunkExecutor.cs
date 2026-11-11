namespace Tralys.Ecs;

/// <summary>One partitioned query run.</summary>
public interface IPartitionJob
{
    /// <summary>Runs chunk <paramref name="item"/> of the run on <paramref name="worker"/>, which is below
    /// <see cref="IChunkExecutor.WorkerCount"/>. A worker index is never used by two threads at once.</summary>
    void Execute(int item, int worker);
}

/// <summary>Runs the chunks of a partitioned query. Any assignment of items to workers, in any order, must give the same world, because
/// commands are ordered by chunk and reductions are combined in chunk order.</summary>
public interface IChunkExecutor
{
    /// <summary>Worker indexes passed to <see cref="IPartitionJob.Execute"/> are in <c>[0, WorkerCount)</c>; a run allocates one
    /// buffer per worker.</summary>
    int WorkerCount { get; }

    /// <summary>Executes every item in <c>[0, itemCount)</c> exactly once and returns after all have finished. When an item throws, items
    /// not yet started are skipped and that exception is rethrown once the started ones have finished.</summary>
    void Run<TJob>(ref TJob job, int itemCount)
        where TJob : struct, IPartitionJob;
}
