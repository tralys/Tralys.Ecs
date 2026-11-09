namespace Tralys.Ecs.Tests;

public sealed class PresenceTests
{
    [Test]
    public async Task A_disabled_component_reads_as_absent_except_to_the_stored_reads()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Burning { Ticks = 5 });
        world.SetEnabled<Burning>(entity, false);

        var read = world.TryGet<Burning>(entity, out _);
        var stored = world.TryGetStored<Burning>(entity, out var kept);
        var hasStored = world.HasStored<Burning>(entity);

        await Assert.That(read).IsFalse();
        await Assert.That(stored && kept.Ticks == 5).IsTrue();
        await Assert.That(hasStored).IsTrue();
        await Assert
            .That(() =>
            {
                using var edit = world.Edit<Burning>(entity);
            })
            .Throws<InvalidOperationException>();
        await Assert
            .That(() => world.Replace(entity, new Burning { Ticks = 6 }))
            .Throws<InvalidOperationException>();
        await Assert.That(world.Remove<Burning>(entity)).IsTrue();
        await Assert.That(world.HasStored<Burning>(entity)).IsFalse();
    }

    [Test]
    public async Task Setting_or_adding_a_disabled_component_writes_and_enables_it()
    {
        using var world = TestWorlds.Create();
        var set = world.Create();
        var added = world.Create();
        var played = world.Create();
        world.Set(set, new Burning { Ticks = 1 });
        world.Add<Frozen>(added);
        world.Set(played, new Burning { Ticks = 1 });
        world.SetEnabled<Burning>(set, false);
        world.SetEnabled<Frozen>(added, false);
        world.SetEnabled<Burning>(played, false);

        world.Set(set, new Burning { Ticks = 2 });
        world.Add<Frozen>(added);
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Set(played, new Burning { Ticks = 3 });
        world.Submit(buffer);
        world.Playback();

        await Assert
            .That(world.TryGet<Burning>(set, out var setNow) ? setNow.Ticks : 0)
            .IsEqualTo(2);
        await Assert.That(world.Has<Frozen>(added)).IsTrue();
        await Assert
            .That(world.TryGet<Burning>(played, out var playedNow) ? playedNow.Ticks : 0)
            .IsEqualTo(3);
    }

    [Test]
    public async Task Disabling_a_component_the_entity_lacks_does_nothing()
    {
        using var world = TestWorlds.Create();
        var direct = world.Create();
        var played = world.Create();

        world.SetEnabled<Frozen>(direct, false);
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.SetEnabled<Frozen>(played, false);
        world.Submit(buffer);
        world.Playback();

        await Assert.That(world.HasStored<Frozen>(direct)).IsFalse();
        await Assert.That(world.HasStored<Frozen>(played)).IsFalse();
    }
}
