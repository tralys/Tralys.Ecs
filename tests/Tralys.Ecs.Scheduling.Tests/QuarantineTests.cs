namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class QuarantineTests
{
    [System]
    sealed partial class Flaky : SystemBase
    {
        public int Runs;

        protected override void OnRun(in RunContext context)
        {
            if (Runs++ % 2 == 0)
                throw new InvalidOperationException("flaked");
        }
    }

    [System]
    sealed partial class Broken : SystemBase
    {
        public int Runs;

        protected override void OnRun(in RunContext context)
        {
            Runs++;
            throw new InvalidOperationException("broken");
        }
    }

    [System]
    sealed partial class Settler : SystemBase
    {
        public Entity Unsettled;
        public readonly List<Entity> Acted = [];

        protected override void OnRun(in RunContext context) => RunScored(context.World);

        [Collect(Act = nameof(Settle))]
        static bool Scored(in Score score) => score.Value > 0;

        void Settle(Entity entity)
        {
            Acted.Add(entity);
            if (entity == Unsettled)
                throw new InvalidOperationException("unsettled");
        }
    }

    [Test]
    public async Task A_throwing_collect_act_is_contained_per_entity_and_never_quarantines()
    {
        using var world = Worlds.Create();
        var faults = new FaultLog();
        world.Faults = faults;
        var settler = new Settler();
        var unsettled = settler.Unsettled = world.Create();
        world.Set(unsettled, new Score { Value = 1 });
        var settled = world.Create();
        world.Set(settled, new Score { Value = 1 });
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Quarantine", [typeof(Early)], [])
        ).Quarantine(after: 1, probeEvery: TimeSpan.FromSeconds(30));
        builder.Add(settler, c => c.InSet<Early>());
        var schedule = builder.Build(world);

        schedule.Run(new Tick(0));
        schedule.Run(new Tick(1));

        var id = settler.GetDescriptor().Id;
        await Assert
            .That(settler.Acted)
            .IsEquivalentTo(
                [unsettled, settled, unsettled, settled],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        await Assert.That(faults.EntityFaults).IsEquivalentTo([(id, unsettled), (id, unsettled)]);
        await Assert.That(faults.Faults).IsEmpty();
        await Assert.That(schedule.Timing.ReadAll()[0].Sample.Faults).IsEqualTo(0);
    }

    [Test]
    public async Task Reset_state_lifts_quarantine_and_clears_timing()
    {
        using var world = Worlds.Create();
        world.Faults = new FaultLog();
        var broken = new Broken();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Quarantine", [typeof(Early)], [])
        ).Quarantine(after: 1, probeEvery: TimeSpan.FromSeconds(30));
        builder.Add(broken, c => c.InSet<Early>());
        var schedule = builder.Build(world);
        schedule.Run(new Tick(0));
        schedule.Run(new Tick(1));

        schedule.ResetState();
        schedule.Run(new Tick(2));

        await Assert.That(broken.Runs).IsEqualTo(2);
        await Assert.That(schedule.Timing.ReadAll()[0].Sample.Faults).IsEqualTo(1);
    }

    [Test]
    public async Task A_system_failing_every_other_run_is_never_quarantined()
    {
        using var world = Worlds.Create();
        var flaky = new Flaky();
        var faults = new FaultLog();
        world.Faults = faults;
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Quarantine", [typeof(Early)], [])
        ).Quarantine(after: 2, probeEvery: TimeSpan.FromSeconds(30));
        builder.Add(flaky, c => c.InSet<Early>());
        var schedule = builder.Build(world);

        for (var tick = 0; tick < 6; tick++)
            schedule.Run(new Tick(tick));

        await Assert.That(flaky.Runs).IsEqualTo(6);
        await Assert.That(faults.Faults.Count).IsEqualTo(3);
    }
}
