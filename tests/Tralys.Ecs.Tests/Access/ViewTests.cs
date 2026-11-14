namespace Tralys.Ecs.Tests;

[View, Part<Position>, Part<Npc>, Part<Health>(Optional = true), Part<Rare>(Optional = true)]
readonly partial struct ScoutView;

public sealed class ViewTests
{
    [Test]
    public async Task A_view_needs_only_its_required_parts_and_its_optional_setters_add_and_remove()
    {
        using var world = TestWorlds.Create();
        var entity = new WorldEntity(world, world.Create());
        entity.Set(new Position { X = 1 });
        var untagged = entity.TryAs<ScoutView>(out _);
        entity.Add<Npc>();
        var matched = entity.TryAs<ScoutView>(out var view);

        view.SetOrRemoveHealth(new Health { Value = 3 });
        view.AddRare();
        var added = (view.Health?.Value, view.Rare);
        view.SetOrRemoveHealth(null);
        view.RemoveRare();

        await Assert
            .That((untagged, matched, added, entity.Has<Health>(), entity.Has<Rare>()))
            .IsEqualTo((false, true, ((int?)3, true), false, false));
    }

    [Test]
    public async Task A_required_part_replace_never_adds_the_part()
    {
        using var world = TestWorlds.Create();
        var entity = new WorldEntity(world, world.Create());
        entity.Set(new Position { X = 1 });
        entity.Add<Npc>();
        entity.TryAs<ScoutView>(out var view);
        entity.Remove<Position>();

        await Assert
            .That(() => view.ReplacePosition(new Position { X = 2 }))
            .Throws<InvalidOperationException>();
        await Assert.That(entity.Has<Position>()).IsFalse();
    }
}
