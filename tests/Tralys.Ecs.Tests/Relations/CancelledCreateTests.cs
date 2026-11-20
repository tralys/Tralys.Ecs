namespace Tralys.Ecs.Tests;

public sealed class CancelledCreateTests
{
    [Test]
    public async Task An_entity_created_and_destroyed_before_playback_leaves_no_edges()
    {
        using var world = TestWorlds.Create();
        var source = world.Create();
        using (world.BeginDelivery())
        {
            var reserved = world.Create();
            world.Relate<Follows>(source, reserved);
            world.Relate<Follows>(reserved, source);
            world.Destroy(reserved);
        }
        world.Playback();

        await Assert.That(world.Targets<Follows>(source).Count).IsEqualTo(0);
        await Assert.That(world.Sources<Follows>(source).Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_entity_created_and_destroyed_before_playback_takes_its_children_with_it()
    {
        using var world = TestWorlds.Create();
        var child = world.Create();
        using (world.BeginDelivery())
        {
            var parent = world.Create();
            world.SetParent(child, parent);
            world.Destroy(parent);
        }
        world.Playback();

        await Assert.That(world.Exists(child)).IsFalse();
    }

    [Test]
    public async Task An_unplaced_child_of_a_dying_parent_leaves_its_unique_index()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        using (world.BeginDelivery())
        {
            var child = world.Create();
            world.Set(child, new Serial { Code = 3 });
            world.SetParent(child, parent);
            world.Destroy(parent);
        }
        world.Playback(finalizeDestroys: false);

        await Assert.That(world.SerialByCode().Contains(3)).IsFalse();
    }

    [Test]
    public async Task Edges_and_rows_on_an_entity_created_and_destroyed_in_a_body_are_never_logged()
    {
        using var world = TestWorlds.Logged();
        var other = world.Create();
        var tail = new LogTail(world);
        using (world.BeginDelivery())
        {
            var created = world.Create();
            world.Relate<Follows>(created, other);
            world.Relate<Follows>(other, created);
            world.EffectRows(created).Set(new Effect { Kind = 1 });
            world.Destroy(created);
        }
        world.Playback();

        var kinds = new List<LogKind>();
        foreach (var entry in tail.Read())
            kinds.Add(entry.Kind);
        await Assert.That(kinds).IsEmpty();
    }
}
