namespace Tralys.Ecs.Tests;

public sealed partial class WorldHarnessTests
{
    readonly record struct Clock(double Seconds) : ITickContext;

    [System]
    sealed partial class Spawner : SystemBase
    {
        public int Remaining;

        protected override void OnRun(in RunContext context)
        {
            if (Remaining-- > 0)
                context.World.Create();
        }
    }

    [System]
    sealed partial class Promoter : SystemBase
    {
        public Entity Holder;
        int rank;

        protected override void OnRun(in RunContext context) =>
            context.World.Replace(Holder, new Medal { Rank = ++rank });
    }

    [System]
    sealed partial class Pulse : SystemBase
    {
        public int Runs;

        protected override void OnRun(in RunContext context) => Runs++;
    }

    static Schedule<Clock> Build<TSystem>(
        World world,
        TSystem system,
        Action<SystemConfig>? configure = null
    )
        where TSystem : SystemBase =>
        new ScheduleBuilder<Clock>(new ScheduleDeclaration("Harness", [], []))
            .Add(system, configure)
            .Build(world);

    [Test]
    public async Task Quiescence_waits_for_every_world_and_ignores_replacements()
    {
        using var spawning = TestWorlds.Create();
        using var promoting = TestWorlds.Create();
        var holder = promoting.Create();
        promoting.Set(holder, new Medal { Rank = 0 });
        var harness = new WorldHarness<Clock>(tick => new Clock(tick))
            .Add(Build(spawning, new Spawner { Remaining = 3 }))
            .Add(Build(promoting, new Promoter { Holder = holder }));

        var ran = harness.RunUntilQuiescent(maxTicks: 10);

        await Assert.That(ran).IsEqualTo(4);
    }

    [Test]
    public async Task Reset_rewinds_the_tick_count_and_each_schedules_cadence()
    {
        using var world = TestWorlds.Create();
        var pulse = new Pulse();
        var harness = new WorldHarness<Clock>(tick => new Clock(tick)).Add(
            Build(world, pulse, c => c.Cadence(TimeSpan.FromSeconds(10)))
        );
        harness.CaptureBaseline();
        harness.Step();

        harness.Reset();
        harness.Step();

        await Assert.That(harness.Tick).IsEqualTo(1);
        await Assert.That(pulse.Runs).IsEqualTo(2);
    }
}
