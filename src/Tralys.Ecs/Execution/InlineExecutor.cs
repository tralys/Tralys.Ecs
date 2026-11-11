namespace Tralys.Ecs;

/// <summary>Runs every item on the calling thread in item order. The reference every other executor must agree with.</summary>
public sealed class InlineExecutor : IChunkExecutor
{
    /// <summary>Shared stateless instance; partitioned queries use it when the world has no executor.</summary>
    public static InlineExecutor Instance { get; } = new();

    /// <inheritdoc/>
    public int WorkerCount => 1;

    /// <inheritdoc/>
    public void Run<TJob>(ref TJob job, int itemCount)
        where TJob : struct, IPartitionJob
    {
        for (var item = 0; item < itemCount; item++)
            job.Execute(item, 0);
    }
}
