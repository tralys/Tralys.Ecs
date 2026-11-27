namespace Tralys.Ecs.Tests;

public sealed class RowIterationTests
{
    [Test]
    public async Task Removing_every_other_row_while_iterating_still_visits_each_row_once()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        for (var kind = 0; kind < 10; kind++)
            world.EffectRows(entity).Set(new Effect { Kind = kind });

        var visited = new List<int>();
        var rows = world.EffectRows(entity);
        using (var enumerator = rows.GetEnumerator())
        {
            while (enumerator.MoveNext())
            {
                visited.Add(enumerator.Current.Kind);
                if (enumerator.Current.Kind % 2 == 0)
                    enumerator.RemoveCurrent();
            }
        }

        await Assert
            .That(visited)
            .IsEquivalentTo(
                Enumerable.Range(0, 10).ToArray(),
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        await Assert.That(world.EffectRows(entity).Count).IsEqualTo(5);
    }

    [Test]
    public async Task An_enumeration_left_open_across_a_reset_does_not_unbalance_later_ones()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        for (var kind = 0; kind < 2; kind++)
            world.EffectRows(entity).Set(new Effect { Kind = kind });
        var baseline = world.Snapshot();

        foreach (var _ in world.EffectRows(entity))
        {
            world.Reset(baseline);
            break;
        }
        var visited = new List<int>();
        using (var enumerator = world.EffectRows(entity).GetEnumerator())
        {
            while (enumerator.MoveNext())
            {
                visited.Add(enumerator.Current.Kind);
                enumerator.RemoveCurrent();
            }
        }

        await Assert.That(visited).IsEquivalentTo([0, 1]);
    }

    [Test]
    public async Task A_reader_walking_rows_outside_a_walk_throws_once_a_row_is_removed_under_it()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        for (var kind = 0; kind < 3; kind++)
            world.EffectRows(entity).Set(new Effect { Kind = kind });

        await Assert
            .That(() => RemoveWhileReading(world, entity))
            .Throws<InvalidOperationException>();
    }

    static void RemoveWhileReading(World world, Entity entity)
    {
        var rows = world.Reader.Rows<int, Effect>(entity).GetEnumerator();
        rows.MoveNext();
        world.EffectRows(entity).Remove(0);
        rows.MoveNext();
    }

    [Test]
    public async Task A_reader_walking_rows_throws_once_the_world_is_cleared_under_it()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        for (var kind = 0; kind < 3; kind++)
            world.EffectRows(entity).Set(new Effect { Kind = kind });

        await Assert
            .That(() => ClearWhileReading(world, entity))
            .Throws<InvalidOperationException>();
    }

    static void ClearWhileReading(World world, Entity entity)
    {
        var rows = world.Reader.Rows<int, Effect>(entity).GetEnumerator();
        rows.MoveNext();
        world.Clear();
        rows.MoveNext();
    }

    [Test]
    public async Task Removing_through_an_enumeration_the_world_was_cleared_under_throws()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.EffectRows(entity).Set(new Effect { Kind = 0 });

        await Assert
            .That(() => RemoveAfterClear(world, entity))
            .Throws<InvalidOperationException>();
    }

    static void RemoveAfterClear(World world, Entity entity)
    {
        using var enumerator = world.EffectRows(entity).GetEnumerator();
        enumerator.MoveNext();
        world.Clear();
        enumerator.RemoveCurrent();
    }
}
