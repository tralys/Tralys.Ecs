namespace Tralys.Ecs.Scheduling.Tests;

public sealed partial class RunSetAloneTests
{
    [System]
    sealed partial class Spawner : SystemBase
    {
        [Query(Parallel = true)]
        static void Spawn(in Score score, Commands commands) =>
            commands.Set(commands.Create(), new Spawned());

        protected override void OnRun(in RunContext context) => RunSpawn(context.World);
    }

    [Query]
    static void Count(in Spawned spawned, [Arg] int[] count) => count[0]++;

    [Test]
    public async Task A_set_run_alone_plays_back_its_commands()
    {
        using var world = Worlds.Create();
        world.Set(world.Create(), new Score { Value = 1 });
        var builder = new ScheduleBuilder<Tick>(
            new ScheduleDeclaration("Alone", [typeof(Early), typeof(Late)], [])
        );
        builder.Add(new Spawner(), c => c.InSet<Late>());

        builder.Build(world).RunSet<Late>(new Tick(0));
        var count = new int[1];
        RunCount(world, count);

        await Assert.That(count[0]).IsEqualTo(1);
    }
}
