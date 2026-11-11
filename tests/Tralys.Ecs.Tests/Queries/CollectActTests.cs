namespace Tralys.Ecs.Tests;

public sealed class CollectActTests
{
    [Test]
    public async Task An_entry_destroyed_by_an_earlier_entrys_act_is_not_acted_on()
    {
        using var world = TestWorlds.Create();
        var reaper = new Reaper { Killer = world.Create(), Victim = world.Create() };
        world.Set(reaper.Killer, new Health { Value = 0 });
        world.Set(reaper.Victim, new Health { Value = 0 });

        reaper.RunSpent(world);

        await Assert.That(reaper.Acted).IsEquivalentTo([reaper.Killer]);
    }

    [Test]
    public async Task A_throwing_act_leaves_no_entries_for_the_next_run()
    {
        using var world = TestWorlds.Create();
        var reaper = new Reaper();
        var entity = world.Create();
        world.Set(entity, new Health { Value = 1 });

        await Assert.That(() => reaper.RunBelow(world, 5)).Throws<InvalidOperationException>();
        reaper.RunBelow(world, 0);

        await Assert.That(reaper.Acted).IsEquivalentTo([entity]);
    }
}

sealed partial class Reaper
{
    public readonly List<Entity> Acted = [];
    public Entity Killer;
    public Entity Victim;

    [Collect(Act = nameof(Kill))]
    internal static bool Spent(in Health health) => health.Value == 0;

    void Kill(WorldEntity entity)
    {
        Acted.Add(entity.Entity);
        if (entity.Entity == Killer)
            entity.World.Destroy(Victim);
    }

    [Collect(Act = nameof(Explode))]
    internal static bool Below(in Health health, [Arg] int threshold) => health.Value < threshold;

    void Explode(Entity entity)
    {
        Acted.Add(entity);
        throw new InvalidOperationException("boom");
    }
}
