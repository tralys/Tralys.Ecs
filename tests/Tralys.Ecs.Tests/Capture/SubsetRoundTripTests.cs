using Tralys.Ecs.Downstream.Fixture;

namespace Tralys.Ecs.Tests;

public sealed class SubsetRoundTripTests
{
    sealed class ByHealth : ICaptureBoundary, IExternalRefTranslator
    {
        public bool Include(World world, Entity candidate) => !world.Has<Health>(candidate);

        public ExternalRef Translate(World world, Entity outside) =>
            new((ulong)world.Get<Health>(outside).Value);
    }

    sealed class Lookup(Dictionary<ulong, Entity> known) : IExternalRefResolver
    {
        public bool TryResolve(in ExternalRef reference, out Entity entity) =>
            known.TryGetValue(reference.Value, out entity);
    }

    [Test]
    public async Task An_extracted_subset_restores_with_inner_refs_remapped_and_outer_refs_translated()
    {
        using var source = TestWorlds.Create();
        var outsider = source.Create();
        source.Set(outsider, new Health { Value = 77 });
        var first = source.Create();
        source.Set(first, new Position { X = 1 });
        var second = source.Create();
        source.Set(second, new Position { X = 2 });
        var leader = source.Create();
        var squad = new Squad { Size = 2 };
        squad.Members[0] = first;
        squad.Members[1] = second;
        source.Set(leader, squad);
        source.Set(leader, new Link { Target = outsider });
        var stops = source.Buffers<int>().Allocate(3);
        source.Buffers<int>().Get(stops)[2] = 8;
        source.Set(leader, new Route { Stops = stops });

        var boundary = new ByHealth();
        var image = source
            .Extract(
                [leader],
                new CaptureOptions { Boundary = boundary, Translator = boundary },
                new LifecycleReason(300)
            )
            .Image;

        using var target = TestWorlds.Create();
        target.Create();
        target.Create();
        var found = target.Create();
        var result = target.Restore(
            image,
            new RestoreOptions(new Lookup(new() { [77] = found }), UnresolvedPolicy.Fail)
        );
        var restored = result.Map(leader);
        Entity[] expectedMembers = [result.Map(first), result.Map(second)];
        var created = result.Created.Length;
        var restoredSquad = target.Get<Squad>(restored);
        Entity[] members = [restoredSquad.Members[0], restoredSquad.Members[1]];
        var link = target.Get<Link>(restored).Target;
        var restoredStops = target.Buffers<int>().Get(target.Get<Route>(restored).Stops).ToArray();

        await Assert.That(created).IsEqualTo(3);
        await Assert.That(members).IsEquivalentTo(expectedMembers);
        await Assert.That(link).IsEqualTo(found);
        await Assert.That(restoredStops).IsEquivalentTo(new[] { 0, 0, 8 });
        await Assert.That(source.Exists(leader) || source.Exists(first)).IsFalse();
        await Assert.That(source.Exists(outsider)).IsTrue();
    }

    [Test]
    public async Task Children_restored_under_an_outside_parent_keep_their_order()
    {
        using var source = TestWorlds.Create();
        var parent = source.Create();
        source.Set(parent, new Health { Value = 5 });
        var first = source.Create();
        var second = source.Create();
        source.SetParent(first, parent);
        source.SetParent(second, parent);
        var boundary = new ByHealth();
        var image = source.Capture(
            [second, first],
            new CaptureOptions { Boundary = boundary, Translator = boundary }
        );

        using var target = TestWorlds.Create();
        var adopter = target.Create();
        var result = target.Restore(image, new RestoreOptions(new Lookup(new() { [5] = adopter })));
        Entity[] expected = [result.Map(first), result.Map(second)];
        var children = target.Children(adopter).ToArray();

        await Assert
            .That(children)
            .IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_capture_with_default_options_takes_children_relation_targets_rows_and_buffers()
    {
        using var source = TestWorlds.Create();
        var root = source.Create();
        source.Set(root, new Position());
        var stops = source.Buffers<int>().Allocate(2);
        source.Buffers<int>().Get(stops)[1] = 5;
        source.Set(root, new Itinerary { Stops = stops });
        source.EffectRows(root).Set(new Effect { Kind = 1, Stacks = 3 });
        var child = source.Create();
        source.SetParent(child, root);
        var followed = source.Create();
        source.Relate<Follows>(root, followed);

        var image = source.Capture([root]);
        using var target = TestWorlds.Create();
        var restored = target.Restore(image).Map(root);
        var captured = image.SourceEntities.ToArray();
        var effects = target.EffectRows(restored).Count;
        var restoredStops = target
            .Buffers<int>()
            .Get(target.Get<Itinerary>(restored).Stops)
            .ToArray();

        await Assert.That(captured).IsEquivalentTo(new[] { root, child, followed });
        await Assert.That(effects).IsEqualTo(1);
        await Assert.That(restoredStops).IsEquivalentTo(new[] { 0, 5 });
    }

    [Test]
    public async Task An_entity_buffer_restores_naming_the_entities_restored_with_it()
    {
        using var source = TestWorlds.Create();
        var first = source.Create();
        var second = source.Create();
        var holder = source.Create();
        var members = source.Buffers<Entity>().Allocate(2);
        source.Buffers<Entity>().Get(members)[0] = first;
        source.Buffers<Entity>().Get(members)[1] = second;
        source.Set(holder, new Roster { Members = members });

        var image = source.Capture([holder]);
        using var target = TestWorlds.Create();
        target.Create();
        var result = target.Restore(image);
        Entity[] expected = [result.Map(first), result.Map(second)];
        var restored = target
            .Buffers<Entity>()
            .Get(target.Get<Roster>(result.Map(holder)).Members)
            .ToArray();

        await Assert.That(result.Created.Length).IsEqualTo(3);
        await Assert
            .That(restored)
            .IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_source_world_resolver_restores_references_to_entities_that_still_live()
    {
        using var world = TestWorlds.Create();
        var kept = world.Create();
        var gone = world.Create();
        var toKept = world.Create();
        world.Set(toKept, new Link { Target = kept });
        var toGone = world.Create();
        world.Set(toGone, new Link { Target = gone });
        var image = world
            .Extract(
                [toKept, toGone],
                new CaptureOptions { Exclude = CaptureExclusions.EntityFields }
            )
            .Image;
        world.Destroy(gone);
        world.Playback();

        var result = world.Restore(image, new RestoreOptions(new SourceWorldResolver(world)));
        var toKeptTarget = world.Get<Link>(result.Map(toKept)).Target;
        var toGoneTarget = world.Get<Link>(result.Map(toGone)).Target;

        await Assert.That(toKeptTarget).IsEqualTo(kept);
        await Assert.That(toGoneTarget).IsEqualTo(default(Entity));
    }

    [Test]
    public async Task Categories_that_keep_a_component_but_drop_its_requirement_are_rejected()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Position { X = 1 });
        world.Set(entity, new Anchored { Length = 1 });
        var persisted = new CaptureOptions { Categories = CategoryMask.Of(0) };

        await Assert.That(() => world.Capture([entity], persisted)).Throws<ArgumentException>();
    }
}
