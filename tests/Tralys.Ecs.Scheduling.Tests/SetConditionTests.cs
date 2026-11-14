namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class SetConditionTests
{
    [System]
    sealed partial class Pulse : SystemBase
    {
        public int Runs;

        protected override void OnRun(in RunContext context) => Runs++;
    }

    [System]
    sealed partial class Steady : SystemBase
    {
        public int Runs;

        protected override void OnRun(in RunContext context) => Runs++;
    }

    static class Due
    {
        public static int Checks;

        public static bool After(in RunContext context, double seconds)
        {
            Checks++;
            return context.Tick<Tick>().Seconds >= seconds;
        }
    }

    [Test]
    public async Task A_set_condition_runs_once_per_set_run_before_its_members_cadence()
    {
        using var world = Worlds.Create();
        var pulse = new Pulse();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Gated", [typeof(Early)], [])
        ).ConfigureSet<Early>(s => s.RunIf(static (in RunContext c) => Due.After(c, 0.5)));
        builder.Add(pulse, c => c.InSet<Early>().Cadence(TimeSpan.FromSeconds(1)));
        builder.Add(new Steady(), c => c.InSet<Early>());
        var schedule = builder.Build(world);

        schedule.Run(new Tick(0));
        schedule.Run(new Tick(0.5));

        await Assert.That((Due.Checks, pulse.Runs)).IsEqualTo((2, 1));
    }

    [Test]
    public async Task A_set_condition_gates_a_member_added_after_the_build_run_with_the_schedule_or_alone()
    {
        using var world = Worlds.Create();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Gated", [typeof(Early)], [])
        ).ConfigureSet<Early>(s => s.RunIf(static (in RunContext _) => false));
        var schedule = builder.Build(world);
        var late = new Pulse();

        schedule.Add(late, c => c.InSet<Early>());
        schedule.Run(new Tick(0));
        schedule.RunSystem<Pulse>(new Tick(1));

        await Assert.That(late.Runs).IsEqualTo(0);
    }

    [System]
    sealed partial class Host(Schedule<Tick> inner) : SystemBase
    {
        protected override void OnRun(in RunContext context) => inner.Run(new Tick(0));
    }

    [Test]
    public async Task A_throwing_set_condition_is_one_set_fault_and_leaves_member_conditions_unchecked()
    {
        using var world = Worlds.Create();
        var faults = new FaultLog();
        world.Faults = faults;
        var pulse = new Pulse();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Gated", [typeof(Early)], [])
        ).ConfigureSet<Early>(s =>
            s.RunIf(static (in RunContext _) => throw new InvalidOperationException("set"))
        );
        builder.Add(
            pulse,
            c =>
                c.InSet<Early>()
                    .RunIf(
                        static (in RunContext _) => throw new InvalidOperationException("member")
                    )
        );
        builder.Add(new Steady(), c => c.InSet<Early>());
        var schedule = builder.Build(world);

        schedule.Run(new Tick(0));

        await Assert
            .That((faults.SetFaults.Count, faults.Faults.Count, pulse.Runs))
            .IsEqualTo((1, 0, 0));
    }

    [Test]
    public async Task A_built_builder_refuses_set_conditions()
    {
        using var world = Worlds.Create();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Gated", [typeof(Early)], [])
        );
        SetConfig? kept = null;
        builder.ConfigureSet<Early>(s => kept = s);
        builder.Build(world);

        await Assert
            .That(() =>
                builder.ConfigureSet<Early>(s => s.RunIf(static (in RunContext _) => false))
            )
            .Throws<InvalidOperationException>();
        await Assert
            .That(() => kept!.RunIf(static (in RunContext _) => false))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_set_condition_inside_another_system_s_run_sees_no_system()
    {
        using var world = Worlds.Create();
        var pulse = new Pulse();
        var inner = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Inner", [typeof(Early)], [])
        ).ConfigureSet<Early>(s =>
            s.RunIf(static (in RunContext c) => c.System.Equals(default(SystemId)))
        );
        inner.Add(pulse, c => c.InSet<Early>());
        var outer = new ScheduleBuilder<Tick>(new ScheduleDeclaration("Outer", [], []));
        outer.Add(new Host(inner.Build(world)));

        outer.Build(world).Run(new Tick(0));

        await Assert.That(pulse.Runs).IsEqualTo(1);
    }
}
