namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class ObserverTests
{
    [System]
    sealed partial class Spawner : SystemBase
    {
        [Query(Parallel = true)]
        static void Spawn(in Score score, Commands commands) =>
            commands.Set(commands.Create(), new Spawned { Generation = score.Value });

        protected override void OnRun(in RunContext context) => RunSpawn(context.World);
    }

    [System]
    sealed partial class Counter : SystemBase
    {
        [Query]
        static void Count(in Spawned spawned) { }

        protected override void OnRun(in RunContext context) => RunCount(context.World);
    }

    [System]
    sealed partial class Gated : SystemBase
    {
        protected override void OnRun(in RunContext context) { }
    }

    sealed class Recorder : IScheduleObserver
    {
        public readonly List<string> Log = [];

        public void RunSet(Type set, ScheduledWork work) => Wrap($"set {set.Name}", work);

        public void RunSystem(SystemDescriptor system, ScheduledWork work) =>
            Wrap($"run {ClassName(system)}", work);

        public void RunPlayback(string point, ScheduledWork work) =>
            Wrap($"playback {point}", work);

        public void OnSystemSkipped(SystemDescriptor system, SystemSkip reason) =>
            Log.Add($"skip {ClassName(system)} {reason}");

        static string ClassName(SystemDescriptor system) => system.Type?.Name ?? system.Name;

        void Wrap(string what, ScheduledWork work)
        {
            Log.Add(what + " {");
            work.Run();
            Log.Add("}");
        }
    }

    [Test]
    public async Task An_observer_wraps_each_set_run_and_playback_and_hears_each_skip()
    {
        using var world = Worlds.Create();
        world.Set(world.Create(), new Score { Value = 1 });
        var recorder = new Recorder();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration(
                "Observed",
                [typeof(Early), typeof(Late)],
                [new PlaybackPointDeclaration("Settle", [typeof(Early)], [])]
            )
        ).Observe(recorder);
        builder.Add(new Spawner(), c => c.InSet<Early>());
        builder.Add(new Counter(), c => c.InSet<Early>());
        builder.Add(new Gated(), c => c.InSet<Late>().RunIf(static (in RunContext _) => false));

        builder.Build(world).Run(new Tick(0));

        await Assert
            .That(string.Join(" ", recorder.Log))
            .IsEqualTo(
                "set Early { run Spawner { } playback AfterSpawner { } run Counter { } } "
                    + "playback Settle { } set Late { skip Gated Condition } playback EndOfTick { }"
            );
    }

    [Test]
    public async Task A_gated_off_set_reports_each_member_skipped_and_still_plays_its_points()
    {
        using var world = Worlds.Create();
        var recorder = new Recorder();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Gated", [typeof(Early)], [])
        )
            .ConfigureSet<Early>(s => s.RunIf(static (in RunContext _) => false))
            .Observe(recorder);
        builder.Add(new Spawner(), c => c.InSet<Early>());
        builder.Add(new Counter(), c => c.InSet<Early>());

        builder.Build(world).Run(new Tick(0));

        await Assert
            .That(string.Join(" ", recorder.Log))
            .IsEqualTo(
                "set Early { skip Spawner Condition skip Counter Condition } playback EndOfTick { }"
            );
    }
}
