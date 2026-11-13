namespace Tralys.Ecs.Tests;

public sealed class OrderedDueTests
{
    [Test]
    public async Task Due_includes_a_deadline_equal_to_its_bound()
    {
        using var world = TestWorlds.Create();
        var early = world.Create();
        world.Set(early, new Deadline { At = 5 });
        var exact = world.Create();
        world.Set(exact, new Deadline { At = 10 });
        var late = world.Create();
        world.Set(late, new Deadline { At = 15 });

        var due = new List<Entity>();
        foreach (var entity in world.DeadlineDue().Due(10.0))
            due.Add(entity);

        await Assert.That(due).IsEquivalentTo([early, exact]);
    }
}
