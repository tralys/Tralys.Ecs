using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

/// <summary>Waking workers and joining them for one small batch of chunk work: thread pool, Parallel.For, and the spin-then-park
/// <see cref="WorkerPool"/>.</summary>
public class ExecutorWakeJoinBench
{
    const int Partitions = 8;
    readonly long[] sums = new long[Partitions * 8];
    readonly WorkerPool pool = new(Math.Min(Environment.ProcessorCount, Partitions) - 1);

    [Params(32, 512)]
    public int RowsPerPartition;

    [GlobalCleanup]
    public void Cleanup() => pool.Dispose();

    void Work(int partition)
    {
        long sum = 0;
        for (var i = 0; i < RowsPerPartition; i++)
            sum += i ^ partition;
        sums[partition * 8] = sum;
    }

    [Benchmark(Baseline = true)]
    public void Inline()
    {
        for (var p = 0; p < Partitions; p++)
            Work(p);
    }

    [Benchmark]
    public void ParallelFor() => Parallel.For(0, Partitions, Work);

    [Benchmark]
    public void SpinThenPark()
    {
        var job = new SumJob(sums, RowsPerPartition);
        pool.Run(ref job, Partitions);
    }

    readonly struct SumJob(long[] sums, int rows) : IPartitionJob
    {
        public void Execute(int item, int worker)
        {
            long sum = 0;
            for (var i = 0; i < rows; i++)
                sum += i ^ item;
            sums[item * 8] = sum;
        }
    }
}
