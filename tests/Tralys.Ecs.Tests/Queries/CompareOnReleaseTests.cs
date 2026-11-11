namespace Tralys.Ecs.Tests;

public sealed partial class CompareOnReleaseTests
{
    [Query]
    static void Nudge(Entity entity, ref Position position, [Arg] Entity target)
    {
        if (entity == target)
            position.X += 1;
    }

    [Test]
    public async Task A_ref_parameter_stamps_only_the_rows_whose_value_changed()
    {
        using var world = TestWorlds.Create();
        var entities = new Entity[100];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create();
            world.Set(entities[i], new Position { X = i });
        }
        var cursor = world.Changes.Register("changes");

        RunNudge(world, entities[42]);

        var changed = new List<Entity>();
        foreach (var row in world.Changes.Sample(cursor).Changed<Position>())
            changed.Add(row.Entity);
        await Assert.That(changed).IsEquivalentTo([entities[42]]);
    }
}
