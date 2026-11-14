namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class DynamicPlaybackTests
{
    sealed class Heard : IAddObserver<Heat>
    {
        public int Count;

        public void OnAdd(in ObserverContext context, Entity entity, in Heat value) => Count++;
    }

    [System]
    sealed partial class Heater(Entity target) : SystemBase
    {
        protected override void OnRun(in RunContext context) =>
            context.World.Set(target, new Heat { Value = 1 });
    }

    [System]
    sealed partial class Listener(Heard heard) : SystemBase
    {
        public int Seen;

        protected override void OnRun(in RunContext context) => Seen = heard.Count;
    }

    sealed class Playbacks : IScheduleObserver
    {
        public readonly List<string> Points = [];

        public void RunSet(Type set, ScheduledWork work) => work.Run();

        public void RunSystem(SystemDescriptor system, ScheduledWork work) => work.Run();

        public void RunPlayback(string point, ScheduledWork work)
        {
            Points.Add(point);
            work.Run();
        }

        public void OnSystemSkipped(SystemDescriptor system, SystemSkip reason) { }
    }

    [Test]
    public async Task A_playback_follows_a_system_that_left_work_for_one_and_no_other()
    {
        using var world = Worlds.Create();
        var heard = new Heard();
        world.Observers.OnAdd(heard);
        var listener = new Listener(heard);
        var playbacks = new Playbacks();
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Dynamic", [typeof(Early)], [])
        ).Observe(playbacks);
        builder.Add(new Heater(world.Create()), c => c.InSet<Early>());
        builder.Add(listener, c => c.InSet<Early>());

        builder.Build(world).Run(new Tick(0));

        await Assert.That(listener.Seen).IsEqualTo(1);
        await Assert
            .That(playbacks.Points)
            .IsEquivalentTo(
                ["AfterHeater", "EndOfTick"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
    }
}
