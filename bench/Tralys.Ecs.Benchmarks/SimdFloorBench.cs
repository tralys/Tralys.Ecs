using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

/// <summary>Vector128, the Android ARM64 NEON width, against Vector256 on the x64 server, for a column-wide update.</summary>
public class SimdFloorBench
{
    float[] xs = [];
    float[] vs = [];

    [GlobalSetup]
    public void Setup()
    {
        xs = new float[32 * 1024];
        vs = new float[32 * 1024];
        new Random(1).NextBytes(MemoryMarshal.AsBytes(vs.AsSpan()));
    }

    [Benchmark(Baseline = true)]
    public void Scalar()
    {
        for (var i = 0; i < xs.Length; i++)
            xs[i] += vs[i] * 0.016f;
    }

    [Benchmark]
    public void Vector128Width()
    {
        var dt = Vector128.Create(0.016f);
        var i = 0;
        for (; i <= xs.Length - Vector128<float>.Count; i += Vector128<float>.Count)
        {
            var x = Vector128.Create(xs.AsSpan(i));
            var v = Vector128.Create(vs.AsSpan(i));
            (x + (v * dt)).CopyTo(xs.AsSpan(i));
        }
    }

    [Benchmark]
    public void Vector256Width()
    {
        if (!Vector256.IsHardwareAccelerated)
            return;
        var dt = Vector256.Create(0.016f);
        var i = 0;
        for (; i <= xs.Length - Vector256<float>.Count; i += Vector256<float>.Count)
        {
            var x = Vector256.Create(xs.AsSpan(i));
            var v = Vector256.Create(vs.AsSpan(i));
            (x + (v * dt)).CopyTo(xs.AsSpan(i));
        }
    }

    [Benchmark]
    public void PortableVector()
    {
        var i = 0;
        for (; i <= xs.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            var x = new Vector<float>(xs, i);
            var v = new Vector<float>(vs, i);
            (x + (v * 0.016f)).CopyTo(xs, i);
        }
    }
}
