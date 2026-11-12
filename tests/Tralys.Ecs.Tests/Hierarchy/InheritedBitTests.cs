namespace Tralys.Ecs.Tests;

public sealed partial class InheritedBitTests
{
    [Query, Without<Disabled>]
    static void Visible(Entity entity, in Position position, [Arg] List<Entity> seen) =>
        seen.Add(entity);

    [Query]
    static void Live(Entity entity, in Position position, [Arg] List<Entity> seen) =>
        seen.Add(entity);

    [Test]
    public async Task A_subtree_moved_under_a_disabled_parent_is_disabled()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        world.Add<Disabled>(parent);
        var child = world.Create();
        world.Set(child, new Position());
        var grandchild = world.Create();
        world.Set(grandchild, new Position());
        world.SetParent(grandchild, child);

        world.SetParent(child, parent);
        var seen = new List<Entity>();
        RunVisible(world, seen);

        await Assert.That(seen).IsEmpty();
    }

    [Test]
    public async Task Cancelling_a_root_keeps_a_descendant_destroyed_on_its_own()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        world.Set(root, new Position());
        var child = world.Create();
        world.Set(child, new Position());
        world.SetParent(child, root);

        world.Destroy(child);
        world.Destroy(root);
        world.CancelDestroy(root);
        var seen = new List<Entity>();
        RunLive(world, seen);
        world.Playback();

        await Assert.That(seen).IsEquivalentTo([root]);
        await Assert.That(world.Exists(child)).IsFalse();
    }

    [Test]
    public async Task Destroying_a_root_again_after_cancelling_hides_its_subtree()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        world.Set(root, new Position());
        var child = world.Create();
        world.Set(child, new Position());
        world.SetParent(child, root);

        world.Destroy(root);
        world.CancelDestroy(root);
        world.Destroy(root);
        var seen = new List<Entity>();
        RunLive(world, seen);

        await Assert.That(seen).IsEmpty();
    }

    [Test]
    public async Task A_flip_recorded_in_a_body_is_in_effect_below_its_holder_at_once()
    {
        using var world = TestWorlds.Create();
        var parent = world.Create();
        var child = world.Create();
        world.SetParent(child, parent);
        world.Add<Disabled>(parent);
        world.SetEnabled<Disabled>(parent, false);

        bool onParent,
            onChild;
        using (world.BeginDelivery())
        {
            world.SetEnabled<Disabled>(parent, true);
            onParent = world.IsEffective<Disabled>(parent);
            onChild = world.IsEffective<Disabled>(child);
        }
        world.Playback();

        await Assert.That((onParent, onChild)).IsEqualTo((true, true));
    }
}
