namespace Tralys.Ecs.Tests;

public sealed class EnableObserverTests
{
    sealed class Records : IAddObserver<Burning>, IRemoveObserver<Burning>
    {
        public readonly List<int> Seen = [];

        public void OnAdd(in ObserverContext context, Entity entity, in Burning value) =>
            Seen.Add(value.Ticks);

        public void OnRemove(in ObserverContext context, Entity entity, in Burning oldValue) =>
            Seen.Add(-oldValue.Ticks);
    }

    static Records Watch(World world)
    {
        var records = new Records();
        world.Observers.OnAdd(records);
        world.Observers.OnRemove(records);
        return records;
    }

    static Entity Disabled(World world, int ticks)
    {
        var entity = world.Create();
        world.Set(entity, new Burning { Ticks = ticks });
        world.SetEnabled<Burning>(entity, false);
        world.Playback();
        return entity;
    }

    [Test]
    public async Task An_enable_is_an_add_and_a_disable_a_removal()
    {
        using var world = TestWorlds.Create();
        var records = Watch(world);
        var entity = world.Create();

        world.Set(entity, new Burning { Ticks = 3 });
        world.SetEnabled<Burning>(entity, false);
        world.Set(entity, new Burning { Ticks = 5 });
        world.SetEnabled<Burning>(entity, false);
        world.Remove<Burning>(entity);
        world.Playback();

        await Assert
            .That(records.Seen)
            .IsEquivalentTo([3, -3, 5, -5], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Recorded_flips_reach_observers_as_the_batch_s_net_change()
    {
        using var world = TestWorlds.Create();
        var disabled = world.Create();
        world.Set(disabled, new Burning { Ticks = 3 });
        var enabled = Disabled(world, 4);
        var flickered = world.Create();
        world.Set(flickered, new Burning { Ticks = 6 });
        world.Playback();
        var records = Watch(world);

        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.SetEnabled<Burning>(disabled, false);
        buffer.Commands.Set(enabled, new Burning { Ticks = 5 });
        buffer.Commands.SetEnabled<Burning>(flickered, false);
        buffer.Commands.Set(flickered, new Burning { Ticks = 7 });
        world.Submit(buffer);
        world.Playback();

        await Assert
            .That(records.Seen)
            .IsEquivalentTo([-3, 5], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_batch_that_leaves_a_component_absent_as_it_found_it_reports_nothing()
    {
        using var world = TestWorlds.Create();
        var enabledThenRemoved = Disabled(world, 3);
        var removed = Disabled(world, 4);
        var flickered = Disabled(world, 6);
        var records = Watch(world);

        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.Set(enabledThenRemoved, new Burning { Ticks = 5 });
        buffer.Commands.Remove<Burning>(enabledThenRemoved);
        buffer.Commands.Remove<Burning>(removed);
        buffer.Commands.Set(flickered, new Burning { Ticks = 7 });
        buffer.Commands.SetEnabled<Burning>(flickered, false);
        buffer.Commands.Remove<Burning>(flickered);
        world.Submit(buffer);
        world.Playback();

        await Assert.That(records.Seen).IsEmpty();
    }

    sealed class SealRecords : IAddObserver<Seal>, IRemoveObserver<Seal>, IReplaceObserver<Seal>
    {
        public readonly List<string> Seen = [];

        public void OnAdd(in ObserverContext context, Entity entity, in Seal value) =>
            Seen.Add($"+{value.Mark}");

        public void OnRemove(in ObserverContext context, Entity entity, in Seal oldValue) =>
            Seen.Add($"-{oldValue.Mark}");

        public void OnReplace(
            in ObserverContext context,
            Entity entity,
            in Seal oldValue,
            in Seal newValue
        ) => Seen.Add($"{oldValue.Mark}>{newValue.Mark}");
    }

    [Test]
    public async Task Setting_a_disabled_replace_only_component_is_an_add_not_a_replace()
    {
        using var world = TestWorlds.Create();
        var direct = world.Create();
        var recorded = world.Create();
        foreach (var entity in (Entity[])[direct, recorded])
        {
            world.Set(entity, new Seal { Mark = 1 });
            world.SetEnabled<Seal>(entity, false);
        }
        world.Playback();
        var records = new SealRecords();
        world.Observers.OnAdd(records);
        world.Observers.OnRemove(records);
        world.Observers.OnReplace(records);

        world.Set(direct, new Seal { Mark = 2 });
        var buffer = world.RentBuffer(world.runSequence, 0);
        buffer.Commands.Set(recorded, new Seal { Mark = 2 });
        buffer.Commands.Set(recorded, new Seal { Mark = 2 });
        world.Submit(buffer);
        world.Playback();

        await Assert.That(records.Seen).IsEquivalentTo(["+2", "+2"]);
    }

    [Test]
    public async Task Destroying_an_entity_reports_no_removal_of_a_disabled_component()
    {
        using var world = TestWorlds.Create();
        var entity = Disabled(world, 3);
        var records = Watch(world);

        world.Destroy(entity);
        world.Playback();

        await Assert.That(records.Seen).IsEmpty();
    }

    [Test]
    public async Task Replay_skips_a_disabled_component()
    {
        using var world = TestWorlds.Create();
        Disabled(world, 3);

        var records = new Records();
        world.Observers.OnAdd(records, replayExisting: true);
        world.Playback();

        await Assert.That(records.Seen).IsEmpty();
    }

    [Test]
    public async Task A_component_restored_disabled_is_logged_disabled_and_not_reported_added()
    {
        using var source = TestWorlds.Create();
        var entity = Disabled(source, 3);
        var image = source.Capture([entity]);
        using var target = TestWorlds.Logged();
        var records = Watch(target);
        var tail = new LogTail(target);

        target.Restore(image);
        target.Playback();

        var kinds = new List<LogKind>();
        foreach (var entry in tail.Read())
        {
            if (entry.Is<Burning>())
                kinds.Add(entry.Kind);
        }
        await Assert
            .That(kinds)
            .IsEquivalentTo(
                [LogKind.Added, LogKind.Disabled],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        await Assert.That(records.Seen).IsEmpty();
    }
}
