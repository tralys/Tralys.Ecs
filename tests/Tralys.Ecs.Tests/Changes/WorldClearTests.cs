namespace Tralys.Ecs.Tests;

public sealed class WorldClearTests
{
    [Test]
    public async Task A_cursor_registered_before_clear_sees_changes_made_after_it()
    {
        using var world = TestWorlds.Create();
        var cursor = world.Changes.Register("clear");
        world.Clear();
        var entity = world.Create();
        world.Set(entity, new Position { X = 1 });

        var rows = 0;
        foreach (var row in world.Changes.Sample(cursor).Changed<Position>())
            rows++;

        await Assert.That(rows).IsEqualTo(1);
    }

    [Test]
    public async Task An_edit_open_across_clear_throws_on_dispose()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Position { X = 1 });

        await Assert.That(DisposeAfterClear(world, entity)).IsTrue();
    }

    static bool DisposeAfterClear(World world, Entity entity)
    {
        var edit = world.Edit<Position>(entity);
        world.Clear();
        try
        {
            edit.Dispose();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [Test]
    public async Task A_handle_taken_before_a_clear_stays_dead_when_its_slot_is_reused()
    {
        using var world = TestWorlds.Create();
        var before = world.Create();
        world.Clear();

        var after = world.Create();

        await Assert
            .That((after.Index, world.Exists(before), after == before))
            .IsEqualTo((before.Index, false, false));
    }
}
