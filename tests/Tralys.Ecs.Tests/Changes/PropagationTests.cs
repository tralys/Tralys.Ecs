namespace Tralys.Ecs.Tests;

public sealed partial class PropagationTests
{
    [Query]
    static void Empower(ref Stats stats) => stats.Power++;

    [Test]
    public async Task A_value_write_below_a_root_raises_its_descendant_version()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var child = world.Create();
        world.Set(child, new Stats { Power = 1 });
        world.SetParent(child, root);
        var before = world.Changes.DescendantVersionOf(root);

        world.AdvanceVersion();
        world.Set(child, new Stats { Power = 5 });

        await Assert.That(world.Changes.DescendantVersionOf(root)).IsGreaterThan(before);
    }

    [Test]
    public async Task A_value_written_through_a_query_ref_below_a_root_raises_its_descendant_version()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var child = world.Create();
        world.Set(child, new Stats { Power = 1 });
        world.SetParent(child, root);
        var before = world.Changes.DescendantVersionOf(root);

        world.AdvanceVersion();
        RunEmpower(world);

        await Assert.That(world.Changes.DescendantVersionOf(root)).IsGreaterThan(before);
    }

    [Test]
    public async Task Every_change_to_what_queries_see_of_a_descendant_raises_its_ancestors_descendant_version()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        var middle = world.Create();
        var leaf = world.Create();
        world.SetParent(middle, root);
        world.SetParent(leaf, middle);
        var missed = new List<string>();

        void Expect(string change, Entity ancestor, Action act)
        {
            world.AdvanceVersion();
            var before = world.Changes.DescendantVersionOf(ancestor);
            act();
            if (world.Changes.DescendantVersionOf(ancestor) <= before)
                missed.Add(change);
        }

        void Recorded(Action<Commands> act)
        {
            var buffer = world.RentBuffer(world.runSequence, 0);
            act(buffer.Commands);
            world.Submit(buffer);
            world.Playback(finalizeDestroys: false);
        }

        Expect("destroy", root, () => world.Destroy(leaf));
        Expect("cancel", root, () => world.CancelDestroy(leaf));
        Expect("recorded destroy", root, () => Recorded(commands => commands.Destroy(leaf)));
        Expect("recorded cancel", root, () => Recorded(commands => commands.CancelDestroy(leaf)));
        Expect("detach", root, () => world.Detach(leaf));
        Expect("recorded reattach", root, () => Recorded(commands => commands.Reattach(leaf)));
        Expect("child disabled through its parent", middle, () => world.Add<Disabled>(middle));
        Expect("child dying through its parent", middle, () => world.Destroy(middle));

        await Assert.That(missed).IsEmpty();
    }

    [Test]
    public async Task Relating_a_source_raises_its_related_version()
    {
        using var world = TestWorlds.Create();
        var source = world.Create();
        var target = world.Create();
        var before = world.Changes.RelatedVersionOf<Follows>(source);

        world.AdvanceVersion();
        world.Relate(source, target, new Follows());

        await Assert.That(world.Changes.RelatedVersionOf<Follows>(source)).IsGreaterThan(before);
    }
}
