namespace Tralys.Ecs.Tests;

public sealed partial class BootstrapTests
{
    public sealed partial class Scorer
    {
        Lookup<Health> health;
        public int Seen;

        [Query]
        internal void Score(Entity entity, in Position position)
        {
            if (health.Has(entity))
                Seen++;
        }
    }

    [Test]
    public async Task A_query_host_that_is_not_a_system_binds_its_lookups()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Position());
        world.Set(entity, new Health { Value = 1 });
        var scorer = new Scorer();

        scorer.RunScore(world);

        await Assert.That(scorer.Seen).IsEqualTo(1);
    }

    sealed class Handmade : SystemBase
    {
        protected override SystemDescriptor CreateDescriptor() => new("Handmade");

        protected override void OnRun(in RunContext context) { }
    }

    [Test]
    public async Task A_handmade_system_keeps_one_descriptor()
    {
        var system = new Handmade();

        await Assert.That(system.GetDescriptor()).IsSameReferenceAs(system.GetDescriptor());
    }
}
