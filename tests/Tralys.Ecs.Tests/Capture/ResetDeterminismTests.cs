using System.Text;
using Tralys.Ecs.Downstream.Fixture;

namespace Tralys.Ecs.Tests;

public sealed partial class ResetDeterminismTests
{
    static List<Entity> Setup(World world)
    {
        var made = new List<Entity>();
        for (var i = 0; i < 6; i++)
            made.Add(world.Create());
        world.Destroy(made[1]);
        world.Destroy(made[4]);
        world.Playback();
        made.RemoveAll(e => !world.Exists(e));
        world.Set(made[0], new Position { X = 1 });
        world.Set(made[0], new Badge { Number = 10 });
        world.Set(made[1], new Dialog { Page = 2 });
        world.Set<Rare>(made[1], default);
        world.Relate<Follows>(made[2], made[0]);
        world.SetParent(made[3], made[0]);
        world.EffectRows(made[2]).Set(new Effect { Kind = 1, Stacks = 1 });
        var stops = world.Buffers<int>().Allocate(2);
        world.Set(made[3], new Route { Stops = stops });
        return made;
    }

    static List<Entity> Script(World world, List<Entity> baseline)
    {
        var made = new List<Entity>();
        for (var i = 0; i < 3; i++)
            made.Add(world.Create());
        world.Destroy(baseline[1]);
        world.Playback();
        made.Add(world.Create());
        world.Set(baseline[0], new Badge { Number = 11 });
        world.Set(made[0], new Badge { Number = 10 });
        world.Relate<Follows>(baseline[3], baseline[2]);
        world.SetParent(made[1], baseline[0], 0);
        world.EffectRows(baseline[2]).Set(new Effect { Kind = 2, Stacks = 5 });
        world.Buffers<int>().Get(world.Get<Route>(baseline[3]).Stops)[1] = 9;
        world.Set(made[2], new Route { Stops = world.Buffers<int>().Allocate(1) });
        made.AddRange(baseline);
        return made;
    }

    static string Fingerprint(World world, List<Entity> entities)
    {
        var text = new StringBuilder();
        foreach (var entity in entities)
        {
            text.Append(entity).Append(' ').Append(world.StateOf(entity));
            if (!world.Exists(entity))
            {
                text.AppendLine();
                continue;
            }
            if (world.Has<Position>(entity))
                text.Append(" pos ").Append(world.Get<Position>(entity).X);
            if (world.Has<Badge>(entity))
                text.Append(" badge ")
                    .Append(world.BadgeByNumber().Get(world.Get<Badge>(entity).Number));
            if (world.Has<Dialog>(entity))
                text.Append(" dialog ").Append(world.Get<Dialog>(entity).Page);
            text.Append(" rare ").Append(world.Has<Rare>(entity));
            foreach (var target in world.Targets<Follows>(entity))
                text.Append(" follows ").Append(target);
            text.Append(" parent ").Append(world.Parent(entity));
            foreach (var child in world.Children(entity))
                text.Append(" child ").Append(child);
            foreach (var row in world.EffectRows(entity))
                text.Append(" effect ").Append(row.Kind).Append('x').Append(row.Stacks);
            if (world.Has<Route>(entity))
                text.Append(" stops ")
                    .AppendJoin(
                        ',',
                        world.Buffers<int>().Get(world.Get<Route>(entity).Stops).ToArray()
                    );
            text.AppendLine();
        }
        return text.ToString();
    }

    [Test]
    public async Task A_reset_world_matches_a_fresh_one_before_and_after_the_same_script()
    {
        using var world = TestWorlds.Create();
        var baseline = Setup(world);
        var snapshot = world.Snapshot();
        var touched = Script(world, baseline);
        world.Reset(snapshot);
        var reset = Fingerprint(world, touched);
        var afterReset = Script(world, baseline);
        var afterScript = Fingerprint(world, afterReset);
        world.Reset(snapshot);
        var resetAgain = Fingerprint(world, touched);

        using var setUp = TestWorlds.Create();
        Setup(setUp);
        using var fresh = TestWorlds.Create();
        var onFresh = Script(fresh, Setup(fresh));

        await Assert.That(reset).IsEqualTo(Fingerprint(setUp, touched));
        await Assert
            .That(afterReset)
            .IsEquivalentTo(onFresh, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(afterScript).IsEqualTo(Fingerprint(fresh, onFresh));
        await Assert.That(resetAgain).IsEqualTo(reset);
    }

    [Query, Changed<Position>]
    static void Moved(Entity entity, [Arg] List<Entity> seen) => seen.Add(entity);

    [Test]
    public async Task The_first_changed_run_after_a_reset_sees_what_it_saw_after_the_snapshot()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Position());
        var snapshot = world.Snapshot();
        var first = new List<Entity>();
        RunMoved(world, first);
        world.Set(entity, new Position { X = 1 });
        RunMoved(world, []);

        world.Reset(snapshot);
        var again = new List<Entity>();
        RunMoved(world, again);

        await Assert.That(again).IsEquivalentTo(first);
        await Assert.That(again).IsNotEmpty();
    }

    [Query]
    static void Placed(Entity entity, in Position position, [Arg] List<Entity> seen) =>
        seen.Add(entity);

    static List<Entity> Spread(World world, bool velocityFirst)
    {
        var first = world.Create();
        world.Set(first, new Position());
        var second = world.Create();
        world.Set(second, new Position());
        if (velocityFirst)
            world.Set(first, new Velocity());
        world.Set(velocityFirst ? second : first, new Health());
        if (!velocityFirst)
            world.Set(second, new Velocity());
        var seen = new List<Entity>();
        RunPlaced(world, seen);
        return seen;
    }

    [Test]
    public async Task Archetypes_made_after_the_snapshot_do_not_outlive_a_reset()
    {
        using var world = TestWorlds.Create();
        var snapshot = world.Snapshot();
        Spread(world, velocityFirst: true);
        world.Reset(snapshot);
        var afterReset = Spread(world, velocityFirst: false);

        using var fresh = TestWorlds.Create();
        var onFresh = Spread(fresh, velocityFirst: false);

        await Assert
            .That(afterReset)
            .IsEquivalentTo(onFresh, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
