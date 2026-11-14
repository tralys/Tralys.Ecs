namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class CadenceTests
{
    [System]
    sealed partial class Pulse : SystemBase
    {
        public int Runs;

        protected override void OnRun(in RunContext context) => Runs++;
    }

    [Test]
    public async Task A_late_tick_runs_a_cadenced_system_once_without_catching_up()
    {
        using var world = Worlds.Create();
        var pulse = new Pulse();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Cadence", [typeof(Early)], [])
        );
        builder.Add(pulse, c => c.InSet<Early>().Cadence(TimeSpan.FromSeconds(1)));
        var schedule = builder.Build(world);

        foreach (var seconds in new[] { 0.0, 3.5, 3.6, 3.7 })
            schedule.Run(new Tick(seconds));

        await Assert.That(pulse.Runs).IsEqualTo(2);
    }
}
