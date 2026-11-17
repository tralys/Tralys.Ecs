namespace Tralys.Ecs.Examples;

public readonly record struct GameTick(double Seconds, float Delta) : ITickContext;

[SystemSet]
public sealed class Simulation;

[SystemSet]
public sealed class Cleanup;

[Schedule, Set<Simulation>, Set<Cleanup>]
public sealed partial class GameSchedule;

[System, InSet<Simulation>]
public sealed partial class MoveSystem : SystemBase
{
    [Query(Parallel = true), Without<Frozen>]
    static void Move(ref Position position, in Velocity velocity, [Arg] float delta)
    {
        position.X += velocity.X * delta;
        position.Y += velocity.Y * delta;
    }

    protected override void OnRun(in RunContext context) =>
        RunMove(context.World, context.Tick<GameTick>().Delta);
}

[System, InSet<Cleanup>]
public sealed partial class CullSystem : SystemBase
{
    [Collect(Act = nameof(Die))]
    static bool Cull(in Unit unit) => unit.Health <= 0;

    static void Die(WorldEntity unit) => unit.Destroy();

    protected override void OnRun(in RunContext context) => RunCull(context.World);
}

[System, InSet<Cleanup>]
public sealed partial class ExpireSystem : SystemBase
{
    [Query(Parallel = true), With<Frozen>]
    static void Expire(Entity entity, in Unit unit, Commands commands)
    {
        if (unit.Health <= 0)
            commands.Destroy(entity);
    }

    protected override void OnRun(in RunContext context) => RunExpire(context.World);
}

public static class Game
{
    public static Schedule<GameTick> BuildSchedule(World world)
    {
        var builder = new ScheduleBuilder<GameTick>(GameSchedule.Declaration);
        builder.Add(new MoveSystem());
        builder.Add(new CullSystem());
        builder.Add(new ExpireSystem());
        return builder.Build(world);
    }

    public static void Step(Schedule<GameTick> schedule, double seconds, float delta) =>
        schedule.Run(new GameTick(seconds, delta));
}
