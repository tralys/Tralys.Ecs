namespace Tralys.Ecs.Tests;

public sealed class LifecycleExclusionTests
{
    [Test]
    public async Task A_cancelled_destroy_puts_the_entity_back_in_its_indexes()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Badge { Number = 5 });

        world.Destroy(entity);
        var hiddenWhilePending = !world.BadgeByNumber().Contains(5);
        world.CancelDestroy(entity);
        world.BadgeByNumber().TryGet(5, out var found);

        await Assert.That(hiddenWhilePending).IsTrue();
        await Assert.That(found).IsEqualTo(entity);
    }

    [Test]
    public async Task A_disabled_component_leaves_its_index_until_enabled_again()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Permit { Code = 3 });

        world.SetEnabled<Permit>(entity, false);
        var hiddenWhileDisabled = !world.PermitByCode().Contains(3);
        world.SetEnabled<Permit>(entity, true);
        world.PermitByCode().TryGet(3, out var found);

        await Assert.That(hiddenWhileDisabled).IsTrue();
        await Assert.That(found).IsEqualTo(entity);
    }

    [Test]
    public async Task Disabling_a_pending_component_leaves_its_index_before_playback()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();

        bool hiddenWhilePending;
        using (world.BeginDelivery())
        {
            world.Set(entity, new Permit { Code = 3 });
            world.SetEnabled<Permit>(entity, false);
            hiddenWhilePending = !world.PermitByCode().Contains(3);
        }
        world.Playback();

        await Assert.That(hiddenWhilePending).IsTrue();
        await Assert.That(world.PermitByCode().Contains(3)).IsFalse();
    }

    [Test]
    public async Task An_extracted_entity_leaves_the_index_of_its_sparse_component()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new SparseLot { Code = 4 });

        world.Extract([entity]);

        await Assert.That(world.SparseLotByCode().Contains(4)).IsFalse();
    }
}
