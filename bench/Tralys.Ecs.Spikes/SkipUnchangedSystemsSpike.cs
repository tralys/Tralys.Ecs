using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Spikes;

/// <summary>D12: checking each system's read columns' chunk maxima against its last run, against just running it.</summary>
public class SkipUnchangedSystemsSpike
{
    ulong[][] maxima = [];
    ulong[] lastRun = [];
    float[][] rows = [];

    [Params(100, 1000)]
    public int Systems;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(7);
        maxima = new ulong[Systems][];
        rows = new float[Systems][];
        lastRun = new ulong[Systems];
        for (var s = 0; s < Systems; s++)
        {
            maxima[s] = new ulong[4];
            rows[s] = new float[32];
            if (random.Next(10) == 0)
                maxima[s][random.Next(4)] = 2;
            lastRun[s] = 1;
        }
    }

    [Benchmark(Baseline = true)]
    public float RunEverySystem()
    {
        var sum = 0f;
        for (var s = 0; s < Systems; s++)
            sum += Body(rows[s]);
        return sum;
    }

    [Benchmark]
    public float SkipIdleSystems()
    {
        var sum = 0f;
        for (var s = 0; s < Systems; s++)
        {
            var changed = false;
            foreach (var max in maxima[s])
                changed |= max > lastRun[s];
            if (changed)
                sum += Body(rows[s]);
        }
        return sum;
    }

    static float Body(float[] values)
    {
        var sum = 0f;
        foreach (var value in values)
            sum += value * 0.5f;
        return sum;
    }
}
