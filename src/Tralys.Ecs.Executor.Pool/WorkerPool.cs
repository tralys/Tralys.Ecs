using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Tralys.Ecs;

/// <summary>Dedicated worker threads for partitioned queries. After a run each worker spins for a short window, so back-to-back runs find
/// it awake, then parks. The calling thread works too, and items are claimed from one counter, so a run uses whichever workers are awake and
/// never waits for a parked one to start. Pair it with a per-query inline threshold so small runs stay on the calling thread.</summary>
public sealed class WorkerPool : IChunkExecutor, IDisposable
{
    interface IRunner
    {
        void Execute(int item, int worker);
    }

    sealed class Runner<TJob> : IRunner
        where TJob : struct, IPartitionJob
    {
        public TJob Job;

        public void Execute(int item, int worker) => Job.Execute(item, worker);
    }

    static class JobType<TJob>
        where TJob : struct, IPartitionJob
    {
        public static readonly int Id = Interlocked.Increment(ref nextJobType) - 1;
    }

    static int nextJobType;

    readonly Thread[] threads;
    readonly SemaphoreSlim wake = new(0);
    readonly long spinTicks;
    readonly IRunner?[] runs = new IRunner?[2];
    readonly int[] counts = new int[2];
    object?[] runners = [];
    long claim;
    int remaining;
    int parked;
    int busy;
    Exception? failure;
    volatile bool stopping;

    /// <param name="workers">Background threads beside the caller; zero means one fewer than the processor count.</param>
    /// <param name="spin">How long an idle worker spins before parking; null means 50 µs.</param>
    public WorkerPool(int workers = 0, TimeSpan? spin = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workers);
        if (workers == 0)
            workers = Math.Max(0, Environment.ProcessorCount - 1);
        spinTicks = (long)(
            (spin ?? TimeSpan.FromMicroseconds(50)).TotalSeconds * Stopwatch.Frequency
        );
        threads = new Thread[workers];
        for (var i = 0; i < workers; i++)
        {
            var worker = i + 1;
            threads[i] = new Thread(() => Loop(worker))
            {
                IsBackground = true,
                Name = $"Tralys.Ecs worker {worker}",
            };
            threads[i].Start();
        }
    }

    /// <summary>The background threads plus the calling thread, which runs as worker 0.</summary>
    public int WorkerCount => threads.Length + 1;

    /// <summary>Runs the items across the workers. A call made while another thread's run is in progress, such as from a second world
    /// sharing this pool, runs its items on its own thread instead.</summary>
    public void Run<TJob>(ref TJob job, int itemCount)
        where TJob : struct, IPartitionJob
    {
        ObjectDisposedException.ThrowIf(stopping, this);
        if (
            threads.Length == 0
            || itemCount <= 1
            || Interlocked.CompareExchange(ref busy, 1, 0) != 0
        )
        {
            for (var item = 0; item < itemCount; item++)
                job.Execute(item, 0);
            return;
        }
        Exception? error;
        var runner = RunnerOf<TJob>();
        try
        {
            runner.Job = job;
            var generation = GenerationOf(Volatile.Read(ref claim)) + 1;
            var slot = (int)(generation & 1);
            runs[slot] = runner;
            counts[slot] = itemCount;
            failure = null;
            Volatile.Write(ref remaining, itemCount);
            Interlocked.Exchange(ref claim, (long)((ulong)generation << 32));
            var sleeping = Volatile.Read(ref parked);
            if (sleeping > 0)
                wake.Release(sleeping);

            Drain(0);
            var spinner = new SpinWait();
            while (Volatile.Read(ref remaining) > 0)
                spinner.SpinOnce(sleep1Threshold: -1);
            job = runner.Job;
            error = failure;
        }
        finally
        {
            runner.Job = default;
            Volatile.Write(ref busy, 0);
        }
        if (error is not null)
            ExceptionDispatchInfo.Throw(error);
    }

    /// <summary>Stops and joins the worker threads; a later <see cref="Run{TJob}"/> throws <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        if (stopping)
            return;
        stopping = true;
        wake.Release(threads.Length);
        foreach (var thread in threads)
            thread.Join();
        wake.Dispose();
    }

    Runner<TJob> RunnerOf<TJob>()
        where TJob : struct, IPartitionJob
    {
        var id = JobType<TJob>.Id;
        if (id >= runners.Length)
            Array.Resize(ref runners, Math.Max(id + 1, runners.Length * 2));
        return (Runner<TJob>)(runners[id] ??= new Runner<TJob>());
    }

    // A claim names its generation, so a worker still holding the previous run's runner can never take an item of the next run.
    bool Drain(int worker)
    {
        var worked = false;
        while (true)
        {
            var observed = Volatile.Read(ref claim);
            var slot = (int)(GenerationOf(observed) & 1);
            var item = (int)observed;
            var runner = runs[slot];
            if (runner is null || item >= counts[slot])
                return worked;
            if (Interlocked.CompareExchange(ref claim, observed + 1, observed) != observed)
                continue;
            worked = true;
            try
            {
                runner.Execute(item, worker);
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref failure, error, null);
                SkipRest(observed);
            }
            Interlocked.Decrement(ref remaining);
        }
    }

    void SkipRest(long claimed)
    {
        var generation = GenerationOf(claimed);
        var count = counts[(int)(generation & 1)];
        var end = (long)((ulong)generation << 32) | (uint)count;
        while (true)
        {
            var observed = Volatile.Read(ref claim);
            if (GenerationOf(observed) != generation || (int)observed >= count)
                return;
            if (Interlocked.CompareExchange(ref claim, end, observed) == observed)
            {
                Interlocked.Add(ref remaining, (int)observed - count);
                return;
            }
        }
    }

    static uint GenerationOf(long claim) => (uint)((ulong)claim >> 32);

    void Loop(int worker)
    {
        var seen = 0u;
        while (!stopping)
        {
            var generation = GenerationOf(Volatile.Read(ref claim));
            if (generation != seen)
            {
                seen = generation;
                Drain(worker);
                continue;
            }
            var start = Stopwatch.GetTimestamp();
            var spinner = new SpinWait();
            while (
                GenerationOf(Volatile.Read(ref claim)) == seen
                && !stopping
                && Stopwatch.GetTimestamp() - start < spinTicks
            )
                spinner.SpinOnce(sleep1Threshold: -1);
            if (GenerationOf(Volatile.Read(ref claim)) != seen || stopping)
                continue;
            Interlocked.Increment(ref parked);
            if (GenerationOf(Volatile.Read(ref claim)) == seen && !stopping)
                wake.Wait();
            Interlocked.Decrement(ref parked);
        }
    }
}
