using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Spikes;

/// <summary>D12: copying a read-only subset for a worker and applying its result at a later point, against doing the work inline.</summary>
[MemoryDiagnoser]
public class BackgroundTaskSpike
{
    readonly float[] positions = new float[16384];
    readonly float[] captured = new float[16384];
    float result;

    [Benchmark(Baseline = true)]
    public float Inline() => Work(positions);

    [Benchmark]
    public async Task<float> CaptureHandOffApply()
    {
        positions.CopyTo(captured, 0);
        result = await Task.Run(() => Work(captured));
        return result;
    }

    static float Work(float[] values)
    {
        var sum = 0f;
        foreach (var value in values)
            sum += MathF.Sqrt(value + 1f);
        return sum;
    }
}
