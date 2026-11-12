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
}
