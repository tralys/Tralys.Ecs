using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

/// <summary>What tracking costs a loop that writes 1% of its rows: compare-on-release, stamping every visited row, or nothing.</summary>
public class StampCostBench
{
    const int Rows = 32 * 1024;
    Small8[] small = [];
    Mid32[] mid = [];
    Big128[] big = [];
    ulong[] stamps = [];
    ulong version;

    [Params(8, 32, 128)]
    public int Bytes;

    [GlobalSetup]
    public void Setup()
    {
        small = new Small8[Rows];
        mid = new Mid32[Rows];
        big = new Big128[Rows];
        stamps = new ulong[Rows];
    }

    [Benchmark(Baseline = true)]
    public void Untracked()
    {
        version++;
        switch (Bytes)
        {
            case 8:
                Loop(small, track: 0);
                break;
            case 32:
                Loop(mid, track: 0);
                break;
            default:
                Loop(big, track: 0);
                break;
        }
    }

    [Benchmark]
    public void CompareOnRelease()
    {
        version++;
        switch (Bytes)
        {
            case 8:
                Loop(small, track: 1);
                break;
            case 32:
                Loop(mid, track: 1);
                break;
            default:
                Loop(big, track: 1);
                break;
        }
    }

    [Benchmark]
    public void StampEveryVisitedRow()
    {
        version++;
        switch (Bytes)
        {
            case 8:
                Loop(small, track: 2);
                break;
            case 32:
                Loop(mid, track: 2);
                break;
            default:
                Loop(big, track: 2);
                break;
        }
    }

    void Loop<T>(T[] rows, int track)
        where T : struct
    {
        for (var i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            var before = track == 1 ? row : default;
            if (i % 100 == 0)
                Unsafe.As<T, long>(ref row) += 1;
            if (track == 1)
            {
                var a = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in before));
                var b = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in row));
                if (!a.SequenceEqual(b))
                    stamps[i] = version;
            }
            else if (track == 2)
                stamps[i] = version;
        }
    }
}
