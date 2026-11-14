using System.Text;

namespace Tralys.Ecs.Scheduling.Tests;

[Schedule, Set<Early>, Set<Late>, PlaybackPoint("AfterEarly", After = typeof(Early))]
public sealed partial class FixtureSchedule;

[System, InSet<Early>]
internal sealed partial class Warm : SystemBase
{
    [Query(Parallel = true)]
    static void Raise(in Score score, ref Heat heat) => heat.Value += score.Value % 7;

    protected override void OnRun(in RunContext context) => RunRaise(context.World);
}

[System, InSet<Early>]
internal sealed partial class Age : SystemBase
{
    [Query(Parallel = true)]
    static void Older(ref Spawned spawned) => spawned.Generation++;

    protected override void OnRun(in RunContext context) => RunOlder(context.World);
}

[System, InSet<Late>]
internal sealed partial class Breed : SystemBase
{
    [Query(Parallel = true)]
    static void Offspring(in Score score, in Heat heat, Commands commands)
    {
        if (heat.Value % 5 != 0)
            return;
        var child = commands.Create();
        commands.Set(child, new Score { Value = score.Value + 1 });
        commands.Set(child, new Heat());
        commands.Set(child, new Spawned());
    }

    protected override void OnRun(in RunContext context) => RunOffspring(context.World);
}

[System, InSet<Late>]
internal sealed partial class Cull : SystemBase
{
    [Query(Parallel = true)]
    static void Overheated(Entity entity, in Heat heat, Commands commands)
    {
        if (heat.Value > 40)
            commands.Destroy(entity);
    }

    protected override void OnRun(in RunContext context) => RunOverheated(context.World);
}

public sealed partial class ScheduleDeterminismTests
{
    [Query]
    static void Dump(
        Entity entity,
        in Score score,
        in Heat heat,
        in Spawned spawned,
        [Arg] StringBuilder text
    ) => text.Append($"{entity}:{score.Value}/{heat.Value}/{spawned.Generation} ");

    [Test]
    public async Task Inline_and_pooled_runs_give_the_same_world()
    {
        var reference = Run(null);
        using var pool = new WorkerPool(workers: 3);

        await Assert.That(Run(pool)).IsEqualTo(reference);
    }

    static string Run(IChunkExecutor? executor)
    {
        using var world = Worlds.Create(executor);
        for (var i = 0; i < 200; i++)
        {
            var entity = world.Create();
            world.Set(entity, new Score { Value = i });
            world.Set(entity, new Heat { Value = i % 11 });
            world.Set(entity, new Spawned());
        }
        var builder = new ScheduleBuilder<Tick>(FixtureSchedule.Declaration);
        builder.Add(new Warm());
        builder.Add(new Age());
        builder.Add(new Breed());
        builder.Add(new Cull());
        var schedule = builder.Build(world);
        for (var tick = 0; tick < 10; tick++)
            schedule.Run(new Tick(tick));
        var text = new StringBuilder();
        RunDump(world, text);
        return text.ToString();
    }
}
