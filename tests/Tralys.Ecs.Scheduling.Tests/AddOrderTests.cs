namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class AddOrderTests
{
    [System]
    sealed partial class Zeta(List<string> log) : SystemBase
    {
        protected override void OnRun(in RunContext context) => log.Add(nameof(Zeta));
    }

    [System]
    sealed partial class Alpha(List<string> log) : SystemBase
    {
        protected override void OnRun(in RunContext context) => log.Add(nameof(Alpha));
    }

    [Test]
    public async Task Systems_of_a_set_run_in_the_order_they_were_added()
    {
        using var world = Worlds.Create();
        var log = new List<string>();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Added", [typeof(Early)], [])
        );
        builder.Add(new Zeta(log), c => c.InSet<Early>());
        builder.Add(new Alpha(log), c => c.InSet<Early>());

        builder.Build(world).Run(new Tick(0));

        await Assert
            .That(log)
            .IsEquivalentTo(["Zeta", "Alpha"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
