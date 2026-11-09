namespace Tralys.Ecs.Tests;

public sealed class DeterministicIdTests
{
    [Test]
    public async Task Two_fresh_worlds_running_one_script_hand_out_the_same_handles()
    {
        await Assert
            .That(Run())
            .IsEquivalentTo(Run(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    static List<Entity> Run()
    {
        using var world = TestWorlds.Create();
        var issued = new List<Entity>();
        var live = new List<Entity>();
        var random = new Random(17);
        for (var tick = 0; tick < 50; tick++)
        {
            for (var i = 0; i < 20; i++)
            {
                var entity = world.Create();
                issued.Add(entity);
                live.Add(entity);
            }
            for (var i = 0; i < 15; i++)
            {
                var at = random.Next(live.Count);
                world.Destroy(live[at]);
                live.RemoveAt(at);
            }
            world.Playback();
        }
        return issued;
    }
}
