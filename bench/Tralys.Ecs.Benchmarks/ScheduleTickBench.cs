using BenchmarkDotNet.Attributes;

namespace Tralys.Ecs.Benchmarks;

/// <summary>What the scheduler adds per tick: four generated systems run through a schedule, one with a run condition and one with a cadence, against the same systems run directly.</summary>
[MemoryDiagnoser]
public partial class ScheduleTickBench
{
    [SystemSet]
    public sealed class Readers;

    readonly record struct Tick(double Seconds) : ITickContext;

    sealed class Sink : IFaultSink
    {
        public void OnSystemFault(SystemDescriptor system, Exception error) { }

        public void OnSetFault(Type set, Exception error) { }

        public void OnEntityFault(SystemId system, Entity entity, Exception error) { }

        public void OnPlaybackFault(string point, Exception error) { }
    }

    [System]
    sealed partial class R1 : SystemBase
    {
        public long Total;

        [Query]
        static void Sum(in BHealth health, [Arg] R1 self) => self.Total += health.Value;

        protected override void OnRun(in RunContext context) => RunSum(context.World, this);
    }

    [System]
    sealed partial class R2 : SystemBase
    {
        public long Total;

        [Query]
        static void Sum(in BHealth health, [Arg] R2 self) => self.Total += health.Value;

        protected override void OnRun(in RunContext context) => RunSum(context.World, this);
    }

    [System]
    sealed partial class R3 : SystemBase
    {
        public long Total;

        [Query]
        static void Sum(in BHealth health, [Arg] R3 self) => self.Total += health.Value;

        protected override void OnRun(in RunContext context) => RunSum(context.World, this);
    }

    [System]
    sealed partial class R4 : SystemBase
    {
        public long Total;

        [Query]
        static void Sum(in BHealth health, [Arg] R4 self) => self.Total += health.Value;

        protected override void OnRun(in RunContext context) => RunSum(context.World, this);
    }

    World world = null!;
    SystemBase[] systems = [];
    Schedule<Tick> schedule = null!;
    double seconds;

    [GlobalSetup]
    public void Setup()
    {
        world = Bench.World();
        for (var i = 0; i < 64; i++)
            world.Create(new PlayerBundle());
        systems = [new R1(), new R2(), new R3(), new R4()];
        world.Faults = new Sink();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Bench", [typeof(Readers)], [])
        ).Quarantine(after: 3, probeEvery: TimeSpan.FromSeconds(1));
        builder.Add((R1)systems[0], c => c.InSet<Readers>());
        builder.Add(
            (R2)systems[1],
            c => c.InSet<Readers>().RunIf(static (in RunContext _) => true)
        );
        builder.Add((R3)systems[2], c => c.InSet<Readers>().Cadence(TimeSpan.FromMilliseconds(1)));
        builder.Add((R4)systems[3], c => c.InSet<Readers>());
        schedule = builder.Build(world);
    }

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    [Benchmark(Baseline = true)]
    public void Direct()
    {
        foreach (var system in systems)
            system.Run(world);
        world.Playback();
    }

    [Benchmark]
    public void Scheduled()
    {
        seconds += 0.016;
        schedule.Run(new Tick(seconds));
    }
}
