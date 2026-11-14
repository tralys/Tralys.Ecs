namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class ReplanTests
{
    [System, InSet<Early>]
    sealed partial class Alpha : SystemBase
    {
        public List<string> Log = null!;

        protected override void OnRun(in RunContext context) => Log.Add(nameof(Alpha));
    }

    [System, InSet<Early>]
    sealed partial class Bravo : SystemBase
    {
        public List<string> Log = null!;

        protected override void OnRun(in RunContext context) => Log.Add(nameof(Bravo));
    }

    [System, InSet<Early>]
    sealed partial class Charlie : SystemBase
    {
        public List<string> Log = null!;

        protected override void OnRun(in RunContext context) => Log.Add(nameof(Charlie));
    }

    [Test]
    public async Task Adding_and_removing_replans_and_a_rejected_add_leaves_the_schedule_as_it_was()
    {
        using var world = Worlds.Create();
        var log = new List<string>();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Replan", [typeof(Early)], [])
        );
        builder.Add(new Alpha { Log = log });
        var schedule = builder.Build(world);

        string[] Tick()
        {
            log.Clear();
            schedule.Run(new Tick(0));
            return [.. log];
        }

        schedule.Add(new Bravo { Log = log });
        var afterAdd = Tick();

        ScheduleBuildException? rejected = null;
        try
        {
            schedule.Add(new Charlie { Log = log }, c => c.InSet<Late>());
        }
        catch (ScheduleBuildException error)
        {
            rejected = error;
        }
        var afterRejected = Tick();
        var removedRejected = schedule.Remove<Charlie>();

        var removed = schedule.Remove<Bravo>();
        var afterRemove = Tick();

        await Assert.That(afterAdd).IsEquivalentTo(["Alpha", "Bravo"], Ordered);
        await Assert.That(rejected).IsNotNull();
        await Assert.That(afterRejected).IsEquivalentTo(["Alpha", "Bravo"], Ordered);
        await Assert.That(removedRejected).IsFalse();
        await Assert.That(removed).IsTrue();
        await Assert.That(afterRemove).IsEquivalentTo(["Alpha"], Ordered);
    }

    const TUnit.Assertions.Enums.CollectionOrdering Ordered = TUnit
        .Assertions
        .Enums
        .CollectionOrdering
        .Matching;
}
