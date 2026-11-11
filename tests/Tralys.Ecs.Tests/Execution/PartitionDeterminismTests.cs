using System.Text;

namespace Tralys.Ecs.Tests;

public partial struct FloatSum : IReduction<FloatSum>
{
    public float Value;

    public static FloatSum Identity => default;

    public static FloatSum Combine(in FloatSum left, in FloatSum right) =>
        new() { Value = left.Value + right.Value };
}

public sealed partial class PartitionDeterminismTests
{
    [Query(Parallel = true)]
    static void Total(in Position position, ref FloatSum sum) => sum.Value += position.X;

    [Query(Parallel = true)]
    static void Step(
        Entity entity,
        ref Position position,
        ref Badge badge,
        in Velocity velocity,
        Commands commands
    )
    {
        position.X += velocity.X;
        // Many rows contest badge 1000, so its holder shows the order key changes were applied in.
        if (velocity.X == 1)
            badge.Number = 1000;
        if (velocity.X != 2)
            return;
        var spawned = commands.Create();
        commands.Set(spawned, new Health { Value = (int)position.X });
        commands.Remove<Velocity>(entity);
    }

    [Query]
    static void Dump(Entity entity, in Position position, [Arg] StringBuilder text) =>
        text.Append($"{entity}:{position.X} ");

    [Query]
    static void DumpHealth(Entity entity, in Health health, [Arg] StringBuilder text) =>
        text.Append($"{entity}:{health.Value} ");

    [Test]
    public async Task Inline_and_shuffled_partitions_give_the_same_world_and_log()
    {
        await Assert
            .That(Run(new ShuffledExecutor(seed: 7)))
            .IsEqualTo(Run(InlineExecutor.Instance));
    }

    // Chunk partials of 1e8, 1, -1e8 and 1 sum to 1 only when combined in chunk order.
    static string Run(IChunkExecutor executor)
    {
        using var world = TestWorlds.Create(new WorldOptions { Executor = executor });
        var cursor = world.Changes.Register("log");
        for (var i = 0; i < 128; i++)
        {
            var entity = world.Create();
            var x =
                (i % 32) != 0
                    ? 0f
                    : (i / 32) switch
                    {
                        0 => 1e8f,
                        2 => -1e8f,
                        _ => 1f,
                    };
            world.Set(entity, new Position { X = x });
            world.Set(entity, new Velocity { X = i % 3 });
            world.Set(entity, new Badge { Number = i });
        }

        var total = RunTotal(world);
        RunStep(world);
        world.Playback();

        var text = new StringBuilder($"{total.Value} | ");
        RunDump(world, text);
        text.Append("| ");
        RunDumpHealth(world, text);
        world.BadgeByNumber().TryGet(1000, out var holder);
        text.Append($"| badge 1000: {holder} | ");
        foreach (var entry in world.Changes.Sample(cursor).Log)
            text.Append($"{entry.Kind}:{entry.Entity}:{entry.TypeIndex} ");
        return text.ToString();
    }
}
