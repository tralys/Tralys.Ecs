namespace Tralys.Ecs.Tests;

public sealed partial class SerialStructureTests
{
    static readonly QueryDesc Healthy = QueryDesc.Builder().Read<Health>().Build();
    static readonly QueryDesc Tagged = QueryDesc.Builder().With<Npc>().Build();

    readonly List<Entity> visited = [];
    readonly List<int> moves = [];

    [Query]
    void Strip(Entity entity, in Health health, World world, [Arg] Entity victim)
    {
        visited.Add(entity);
        world.Remove<Health>(victim);
    }

    readonly List<int> seen = [];

    [Query]
    void Readd(Entity entity, ref Health health, World world, [Arg] Entity later)
    {
        seen.Add(health.Value);
        if (entity == later)
        {
            health.Value += 10;
            return;
        }
        world.Remove<Health>(later);
        world.Set(later, new Health { Value = 5 });
    }

    [Query]
    void Mark(Entity entity, in Health health, World world)
    {
        world.Add<Npc>(entity);
        moves.Add(world.structuralCount);
    }

    [Test]
    public async Task A_body_removing_a_later_entitys_matched_component_does_not_visit_it()
    {
        using var world = TestWorlds.Create();
        var first = Spawn(world);
        var victim = Spawn(world);

        RunStrip(world, victim);
        List<Entity> generated = [.. visited];
        visited.Clear();
        world.Set(victim, new Health { Value = 1 });
        foreach (var chunk in world.Query(Healthy))
        foreach (var row in chunk.Rows)
        {
            visited.Add(chunk.Entities[row]);
            world.Remove<Health>(victim);
        }

        await Assert.That(generated).IsEquivalentTo([first]);
        await Assert.That(visited).IsEquivalentTo([first]);
    }

    [Test]
    public async Task A_body_destroying_a_later_entitys_parent_does_not_visit_it_unless_a_cancel_rescues_it()
    {
        using var world = TestWorlds.Create();
        var parent = Spawn(world);
        var doomed = Spawn(world);
        var rescued = Spawn(world);
        world.SetParent(doomed, parent);
        world.SetParent(rescued, parent);
        var counted = -1;

        foreach (var chunk in world.Query(Healthy))
        foreach (var row in chunk.Rows)
        {
            var entity = chunk.Entities[row];
            visited.Add(entity);
            if (entity != parent)
                continue;
            world.Destroy(parent);
            world.CancelDestroy(rescued);
            counted = world.Query(Healthy).Count();
        }

        await Assert.That(visited).IsEquivalentTo([parent, rescued]);
        await Assert.That(counted).IsEqualTo(1);
        await Assert.That(world.StateOf(doomed)).IsEqualTo(LifecycleState.PendingDestroy);
        await Assert.That(world.StateOf(rescued)).IsEqualTo(LifecycleState.Alive);
        await Assert.That(world.Parent(rescued).IsDefault).IsTrue();
    }

    [Test]
    public async Task A_later_row_reads_and_writes_the_value_a_body_recorded_for_it()
    {
        using var world = TestWorlds.Create();
        Spawn(world);
        var later = Spawn(world);

        RunReadd(world, later);

        await Assert.That(seen).IsEquivalentTo([1, 5]);
        await Assert.That(world.Get<Health>(later).Value).IsEqualTo(15);
    }

    [Test]
    public async Task A_structural_change_in_a_serial_body_applies_when_the_walk_closes()
    {
        using var world = TestWorlds.Create();
        Spawn(world);
        Spawn(world);
        var before = world.structuralCount;

        RunMark(world);

        await Assert.That(moves).IsEquivalentTo([before, before]);
        await Assert.That(world.Query(Tagged).Count()).IsEqualTo(2);
    }

    static Entity Spawn(World world)
    {
        var entity = world.Create();
        world.Set(entity, new Health { Value = 1 });
        return entity;
    }
}
