namespace Tralys.Ecs.Tests;

public sealed partial class SerialStructureTests
{
    static void Mark(World world, Entity entity) => world.Add<Npc>(entity);

    [Query]
    static void Touch(Entity entity, in Health health, World world) => Mark(world, entity);

    sealed class Rescues : IDestroyObserver
    {
        public void OnDestroy(in ObserverContext context, Entity entity) =>
            context.World.CancelDestroy(entity);
    }

    [Test]
    public async Task A_helper_changing_structure_in_a_serial_walk_throws_while_a_destroy_observer_still_rescues()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Health { Value = 1 });
        world.Observers.OnDestroy(new Rescues());

        await Assert
            .That(() => RunTouch(world))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("[Collect]");
        world.Destroy(entity);
        world.Playback();

        await Assert.That((world.Has<Npc>(entity), world.IsLive(entity))).IsEqualTo((false, true));
    }
}
