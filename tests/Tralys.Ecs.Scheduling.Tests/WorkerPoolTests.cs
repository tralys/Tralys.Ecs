namespace Tralys.Ecs.Scheduling.Tests;

public sealed class WorkerPoolTests
{
    struct Counted : IPartitionJob
    {
        public int[] Hits;
        public CountdownEvent? Started;
        public ManualResetEventSlim? Gate;

        public void Execute(int item, int worker)
        {
            Started?.Signal();
            Gate?.Wait();
            Interlocked.Increment(ref Hits[item]);
        }
    }

    [Test]
    public async Task A_run_from_a_second_thread_leaves_the_busy_run_s_items_alone()
    {
        using var pool = new WorkerPool(workers: 1);
        using var started = new CountdownEvent(2);
        using var gate = new ManualResetEventSlim();
        var first = new Counted
        {
            Hits = new int[2],
            Started = started,
            Gate = gate,
        };
        var firstHits = first.Hits;
        var running = Task.Run(() => pool.Run(ref first, 2));
        var bothStarted = started.Wait(TimeSpan.FromSeconds(10));

        var second = new Counted { Hits = new int[4] };
        pool.Run(ref second, 4);
        gate.Set();
        await running;

        await Assert.That(bothStarted).IsTrue();
        await Assert.That(firstHits).IsEquivalentTo([1, 1]);
        await Assert.That(second.Hits).IsEquivalentTo([1, 1, 1, 1]);
    }
}
