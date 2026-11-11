namespace Tralys.Ecs.Tests;

public sealed partial class TermMatchingTests
{
    readonly List<Entity> visited = [];
    readonly List<(Entity, bool)> seen = [];

    [Query, AnyOf<Velocity>(Group = 0), AnyOf<Health>(Group = 0)]
    void Either(Entity entity, in Position position) => visited.Add(entity);

    [Query]
    void Guarded(Entity entity, in Position position, Optional<Health> health) =>
        seen.Add((entity, health.HasValue));

    [Test]
    public async Task An_any_of_group_matches_entities_holding_one_member()
    {
        using var world = TestWorlds.Create();
        var both = Spawn(world, velocity: true, health: true);
        var moving = Spawn(world, velocity: true, health: false);
        var healthy = Spawn(world, velocity: false, health: true);
        Spawn(world, velocity: false, health: false);

        RunEither(world);

        await Assert.That(visited).IsEquivalentTo([both, moving, healthy]);
    }

    [Test]
    public async Task An_optional_term_visits_entities_lacking_it()
    {
        using var world = TestWorlds.Create();
        var healthy = Spawn(world, velocity: false, health: true);
        var bare = Spawn(world, velocity: false, health: false);

        RunGuarded(world);

        await Assert.That(seen).IsEquivalentTo([(healthy, true), (bare, false)]);
    }

    static Entity Spawn(World world, bool velocity, bool health)
    {
        var entity = world.Create();
        world.Set(entity, new Position());
        if (velocity)
            world.Set(entity, new Velocity());
        if (health)
            world.Set(entity, new Health());
        return entity;
    }
}
