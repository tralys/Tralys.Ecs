using System.Diagnostics;

namespace Tralys.Ecs;

/// <summary>Steady-state allocation checks for tick tests: warm the work up, then measure what each further call allocates.</summary>
public static class AllocationAssert
{
    /// <summary>Bytes allocated per call of <paramref name="tick"/> in steady state, rounded up so any allocation shows. After
    /// <paramref name="warmup"/> calls it measures rounds of <paramref name="calls"/> calls: two allocation-free rounds in a row give 0,
    /// otherwise it keeps going for <paramref name="settle"/> (default one second), so allocations of code the JIT has not yet optimized do
    /// not count, and returns the last round. Counts the calling thread only unless <paramref name="allThreads"/> is set; then any thread
    /// allocating meanwhile, workers or not, adds to the count.</summary>
    public static long BytesPerCall(
        Action tick,
        int warmup = 64,
        int calls = 256,
        bool allThreads = false,
        TimeSpan? settle = null
    )
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentOutOfRangeException.ThrowIfNegative(warmup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(calls);
        for (var i = 0; i < warmup; i++)
            tick();
        var budget = settle ?? TimeSpan.FromSeconds(1);
        var started = Stopwatch.GetTimestamp();
        var quiet = 0;
        while (true)
        {
            var before = Allocated(allThreads);
            for (var i = 0; i < calls; i++)
                tick();
            var perCall = (Allocated(allThreads) - before + calls - 1) / calls;
            quiet = perCall == 0 ? quiet + 1 : 0;
            if (quiet == 2 || Stopwatch.GetElapsedTime(started) >= budget)
                return perCall;
        }
    }

    /// <summary>Throws <see cref="AllocationAssertException"/> unless <paramref name="tick"/> allocates nothing per call in steady state,
    /// measured as <see cref="BytesPerCall"/> does.</summary>
    public static void SteadyStateAllocatesNothing(
        Action tick,
        int warmup = 64,
        int calls = 256,
        bool allThreads = false,
        TimeSpan? settle = null
    )
    {
        var bytes = BytesPerCall(tick, warmup, calls, allThreads, settle);
        if (bytes != 0)
            throw new AllocationAssertException(bytes);
    }

    static long Allocated(bool allThreads) =>
        allThreads
            ? GC.GetTotalAllocatedBytes(precise: true)
            : GC.GetAllocatedBytesForCurrentThread();
}

/// <summary>Thrown by <see cref="AllocationAssert.SteadyStateAllocatesNothing"/> when the measured call allocated.</summary>
public sealed class AllocationAssertException(long bytesPerCall)
    : Exception($"Allocated {bytesPerCall} bytes per call after warm-up; expected none.")
{
    /// <summary>The bytes each measured call allocated after warm-up.</summary>
    public long BytesPerCall { get; } = bytesPerCall;
}
