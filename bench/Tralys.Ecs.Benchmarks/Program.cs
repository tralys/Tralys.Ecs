using BenchmarkDotNet.Running;
using Tralys.Ecs;
using Tralys.Ecs.Benchmarks;

if (args is ["--probe"])
{
    var churn = new StructuralChurnBench();
    churn.Setup();
    var playback = new PlaybackBench { Chunks = 32 };
    playback.Setup();
    var systems = new ManySystemsFewEntitiesBench { Systems = 100 };
    systems.Setup();
    var indexes = new IndexBench();
    indexes.Setup();
    var due = new DueRatioBench { DuePerTick = 100 };
    due.Setup();
    var schedule = new ScheduleTickBench();
    var introspection = new IntrospectionBench();
    introspection.Setup();
    var results = new (string Name, long Bytes)[]
    {
        ("StructuralChurnBench.Tick", AllocationAssert.BytesPerCall(churn.Tick)),
        ("PlaybackBench.RecordAndPlay", AllocationAssert.BytesPerCall(playback.RecordAndPlay)),
        ("ManySystemsFewEntitiesBench.Tick", AllocationAssert.BytesPerCall(systems.Tick)),
        (
            "ManySystemsFewEntitiesBench.TickGenerated",
            AllocationAssert.BytesPerCall(systems.TickGenerated)
        ),
        ("IndexBench.UniqueIndexRekey", AllocationAssert.BytesPerCall(indexes.UniqueIndexRekey)),
        ("DueRatioBench.OrderedIndexDue", AllocationAssert.BytesPerCall(due.OrderedIndexDue)),
        ("ScheduleTickBench.Scheduled", AllocationAssert.BytesPerCall(schedule.Scheduled)),
        ("IntrospectionBench.Read", AllocationAssert.BytesPerCall(() => introspection.Read())),
    };
    foreach (var (name, bytes) in results)
        Console.WriteLine($"{name}: {bytes} B/tick after warm-up");
    return results.Any(r => r.Bytes != 0) ? 1 : 0;
}

BenchmarkSwitcher.FromAssembly(typeof(Bench).Assembly).Run(args);
return 0;
