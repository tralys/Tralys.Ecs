using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

/// <summary>Pinned managed arrays against native memory for unmanaged columns: iteration and the allocate/free cost of many worlds' columns.</summary>
public unsafe class ColumnBackingBench
{
    const int Rows = 64 * 1024;
    BPosition[] pinned = [];
    BPosition* native;

    [GlobalSetup]
    public void Setup()
    {
        pinned = GC.AllocateArray<BPosition>(Rows, pinned: true);
        native = (BPosition*)NativeMemory.AlignedAlloc((nuint)(Rows * sizeof(BPosition)), 64);
        new Span<BPosition>(native, Rows).Clear();
    }

    [GlobalCleanup]
    public void Cleanup() => NativeMemory.AlignedFree(native);

    [Benchmark(Baseline = true)]
    public float IteratePinned()
    {
        var sum = 0f;
        foreach (ref readonly var p in pinned.AsSpan())
            sum += p.X;
        return sum;
    }

    [Benchmark]
    public float IterateNative()
    {
        var sum = 0f;
        foreach (ref readonly var p in new ReadOnlySpan<BPosition>(native, Rows))
            sum += p.X;
        return sum;
    }

    [Benchmark]
    public int AllocatePinnedColumns()
    {
        var total = 0;
        for (var i = 0; i < 2000; i++)
            total += GC.AllocateArray<BPosition>(32, pinned: true).Length;
        return total;
    }

    [Benchmark]
    public int AllocateNativeColumns()
    {
        var total = 0;
        for (var i = 0; i < 2000; i++)
        {
            var block = NativeMemory.AlignedAlloc((nuint)(32 * sizeof(BPosition)), 64);
            total += 32;
            NativeMemory.AlignedFree(block);
        }
        return total;
    }
}
