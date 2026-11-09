namespace Tralys.Ecs.Tests;

public sealed class StaleHandleTests
{
    [Test]
    public async Task A_handle_to_a_recycled_slot_reads_nothing_of_the_new_entity()
    {
        using var world = TestWorlds.Create();
        var old = world.Create();
        world.Destroy(old);
        world.Playback();
        var reuser = world.Create();
        world.Set(reuser, new Health { Value = 5 });
        var found = world.TryGet<Health>(old, out _);

        await Assert.That(reuser.Index).IsEqualTo(old.Index);
        await Assert.That(world.Exists(old)).IsFalse();
        await Assert.That(world.Has<Health>(old)).IsFalse();
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task A_handle_cannot_be_built_with_the_placeholder_bit_set()
    {
        await Assert
            .That(() => new Entity(5 | Entity.PlaceholderBit, 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task An_unbound_world_entity_reports_absence_and_rejects_writes_like_a_dead_handle()
    {
        var unbound = default(WorldEntity);

        unbound.Destroy();
        var rows = (unbound.EffectRows().Count, unbound.EffectRows().Remove(1));

        await Assert.That(rows).IsEqualTo((0, false));
        await Assert
            .That(() => unbound.EffectRows().Set(new Effect { Kind = 1 }))
            .Throws<InvalidOperationException>();
        await Assert.That(unbound.Has<Health>()).IsFalse();
        await Assert.That(unbound.Remove<Health>()).IsFalse();
        await Assert.That(() => unbound.Get<Health>()).Throws<InvalidOperationException>();
        await Assert
            .That(() => unbound.Set(new Health { Value = 1 }))
            .Throws<InvalidOperationException>();
    }
}
