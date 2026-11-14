namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class RunFinalizeTests
{
    [System]
    sealed partial class Reaper : SystemBase
    {
        public Entity Target;

        protected override void OnRun(in RunContext context) => context.World.Destroy(Target);
    }

    [Test]
    public async Task Running_one_system_finalises_its_destroys_unless_told_not_to()
    {
        using var world = Worlds.Create();
        var reaper = new Reaper { Target = world.Create() };
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Finalize", [typeof(Early)], [])
        );
        builder.Add(reaper, c => c.InSet<Early>());
        var schedule = builder.Build(world);

        schedule.RunSystem<Reaper>(new Tick(0), finalizeDestroys: false);
        var held = world.StateOf(reaper.Target);
        schedule.RunSystem<Reaper>(new Tick(1));

        await Assert
            .That((held, world.StateOf(reaper.Target)))
            .IsEqualTo((LifecycleState.PendingDestroy, LifecycleState.Dead));
    }

    [System]
    sealed partial class LateReaper : SystemBase
    {
        public Entity Target;

        protected override void OnRun(in RunContext context) => context.World.Destroy(Target);
    }

    [Test]
    public async Task A_finalising_point_takes_over_from_the_end_of_the_tick()
    {
        using var world = Worlds.Create();
        var early = new Reaper { Target = world.Create() };
        var late = new LateReaper { Target = world.Create() };
        var builder = new ScheduleBuilder<Tick>(SettlingSchedule.Declaration);
        builder.Add(early, c => c.InSet<SettleFirst>());
        builder.Add(late, c => c.InSet<SettleLast>());

        builder.Build(world).Run(new Tick(0));

        await Assert
            .That((world.StateOf(early.Target), world.StateOf(late.Target)))
            .IsEqualTo((LifecycleState.Dead, LifecycleState.PendingDestroy));
    }
}

[SystemSet]
public sealed class SettleFirst;

[SystemSet]
public sealed class SettleLast;

[
    Schedule,
    Set<SettleFirst>,
    Set<SettleLast>,
    PlaybackPoint("Settle", After = typeof(SettleFirst), FinalizesDestroys = true)
]
public sealed partial class SettlingSchedule;
