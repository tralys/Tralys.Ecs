namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class ScheduleOwnershipTests
{
    [System, InSet<Late>]
    sealed partial class Bravo : SystemBase
    {
        public List<string> Log = null!;

        protected override void OnRun(in RunContext context) => Log.Add(nameof(Bravo));
    }

    [System, InSet<Late>]
    sealed partial class Yankee : SystemBase
    {
        public List<string> Log = null!;

        protected override void OnRun(in RunContext context) => Log.Add(nameof(Yankee));
    }

    [Test]
    public async Task A_throwing_run_condition_reaches_the_sink_and_the_tick_goes_on()
    {
        using var world = Worlds.Create();
        var faults = new FaultLog();
        world.Faults = faults;
        var log = new List<string>();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Gated", [typeof(Late)], [])
        );
        builder.Add(
            new Bravo { Log = log },
            c =>
                c.RunIf(
                    static (in RunContext context) =>
                        throw new InvalidOperationException("gate failed")
                )
        );
        builder.Add(new Yankee { Log = log });

        builder.Build(world).Run(new Tick(0));

        await Assert.That((faults.Faults.Count, log.Count, log[0])).IsEqualTo((1, 1, "Yankee"));
    }

    [System]
    sealed partial class Reaper : SystemBase
    {
        public Entity Target;

        protected override void OnRun(in RunContext context) => context.World.Destroy(Target);
    }

    [Test]
    public async Task Running_a_set_leaves_finalising_to_the_schedule_s_point()
    {
        using var world = Worlds.Create();
        var reaper = new Reaper { Target = world.Create() };
        var builder = new ScheduleBuilder<Tick>(SettlingSchedule.Declaration);
        builder.Add(reaper, c => c.InSet<SettleLast>());

        builder.Build(world).RunSet<SettleLast>(new Tick(0));

        await Assert.That(world.StateOf(reaper.Target)).IsEqualTo(LifecycleState.PendingDestroy);
    }
}
