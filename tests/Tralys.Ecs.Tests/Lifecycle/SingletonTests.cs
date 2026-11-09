namespace Tralys.Ecs.Tests;

public sealed class SingletonTests
{
    [Test]
    public async Task A_conflicting_singleton_bundle_throws_before_creating_a_row()
    {
        using var world = TestWorlds.Create();
        world.Create(new Timekeeper { Clock = new Clock { Tick = 1 } });

        await Assert
            .That(() => world.Create(new Timekeeper { Clock = new Clock { Tick = 2 } }))
            .Throws<InvalidOperationException>();
        var clock = TestWorlds.Registry.Get<Clock>().Index;
        var rows = world.archetypes.Where(a => a.Signature.Has(clock)).Sum(a => a.Count);
        await Assert.That(rows).IsEqualTo(1);
    }

    [Test]
    public async Task A_singleton_added_and_removed_before_playback_leaves_no_holder()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();

        using (world.BeginDelivery())
        {
            world.Set(entity, new Clock { Tick = 1 });
            world.Remove<Clock>(entity);
        }
        world.Playback();

        await Assert.That(world.HasSingleton<Clock>()).IsFalse();
    }

    [Test]
    public async Task A_disabled_singleton_reads_as_absent_yet_keeps_its_claim()
    {
        using var world = TestWorlds.Create();
        var holder = world.Create();
        world.Set(holder, new Weather { Kind = 1 });

        world.SetEnabled<Weather>(holder, false);

        await Assert
            .That((world.HasSingleton<Weather>(), world.SingletonEntity<Weather>()))
            .IsEqualTo((false, holder));
        await Assert
            .That(() => world.Set(world.Create(), new Weather { Kind = 2 }))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Restoring_a_second_holder_of_a_sparse_singleton_tag_throws()
    {
        using var source = TestWorlds.Create();
        var leader = source.Create();
        source.Add<Leader>(leader);
        var image = source.Capture([leader]);
        using var world = TestWorlds.Create();
        world.Add<Leader>(world.Create());

        await Assert.That(() => world.Restore(image)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Removing_a_sparse_singleton_tag_releases_the_claim()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();

        world.Add<Leader>(entity);
        world.Remove<Leader>(entity);

        await Assert.That(world.HasSingleton<Leader>()).IsFalse();
    }

    [Test]
    public async Task A_singleton_moves_to_another_entity_inside_one_iteration()
    {
        using var world = TestWorlds.Create();
        var from = world.Create();
        var to = world.Create();
        world.Set(from, new Clock { Tick = 1 });

        using (world.BeginDelivery())
        {
            world.Remove<Clock>(from);
            world.Set(to, new Clock { Tick = 2 });
        }
        world.Playback();

        await Assert.That(world.Has<Clock>(to)).IsTrue();
        await Assert.That(world.GetSingleton<Clock>().Tick).IsEqualTo(2L);
    }
}
