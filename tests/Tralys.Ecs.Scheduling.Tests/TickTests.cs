namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class TickTests
{
    [System]
    sealed partial class Clock : SystemBase
    {
        public double Seen = -1;

        protected override void OnRun(in RunContext context) => Seen = context.Tick<Tick>().Seconds;
    }

    [Test]
    public async Task Systems_and_run_conditions_read_the_tick_the_schedule_runs_with()
    {
        using var world = Worlds.Create();
        var clock = new Clock();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Ticks", [typeof(Early)], [])
        );
        builder.Add(
            clock,
            c =>
                c.InSet<Early>()
                    .RunIf(static (in RunContext context) => context.Tick<Tick>().Seconds > 1)
        );
        var schedule = builder.Build(world);

        schedule.Run(new Tick(0.5));
        var skipped = clock.Seen;
        schedule.Run(new Tick(2.5));

        await Assert.That((skipped, clock.Seen)).IsEqualTo((-1.0, 2.5));
    }

    [System]
    sealed partial class Gated : SystemBase
    {
        public int Runs;

        protected override void OnRun(in RunContext context) => Runs++;
    }

    [Test]
    public async Task A_shared_run_condition_sees_the_system_it_gates()
    {
        using var world = Worlds.Create();
        var clock = new Clock();
        var gated = new Gated();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Gates", [typeof(Early)], [])
        );
        static bool OnlyGated(in RunContext c) => c.System == Gated.Descriptor.Id;
        builder.Add(clock, c => c.InSet<Early>().RunIf(OnlyGated));
        builder.Add(gated, c => c.InSet<Early>().RunIf(OnlyGated));

        builder.Build(world).Run(new Tick(1));

        await Assert.That((clock.Seen, gated.Runs)).IsEqualTo((-1.0, 1));
    }
}
