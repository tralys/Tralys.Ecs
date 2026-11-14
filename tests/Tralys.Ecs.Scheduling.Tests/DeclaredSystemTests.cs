namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class DeclaredSystemTests
{
    [System, InSet<Early>]
    sealed partial class Added : SystemBase
    {
        protected override void OnRun(in RunContext context) { }
    }

    [System, InSet<Early>]
    sealed partial class Forgotten : SystemBase
    {
        protected override void OnRun(in RunContext context) { }
    }

    [Test]
    public async Task A_declared_system_left_out_fails_the_build()
    {
        using var world = Worlds.Create();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration(
                "Declared",
                [typeof(Early)],
                [],
                [typeof(Added), typeof(Forgotten)]
            )
        );
        builder.Add(new Added());

        var rejected = Assert.Throws<ScheduleBuildException>(() => builder.Build(world));

        await Assert.That(rejected.Problems.Count).IsEqualTo(1);
        await Assert.That(rejected.Problems[0]).Contains(nameof(Forgotten));
    }
}
