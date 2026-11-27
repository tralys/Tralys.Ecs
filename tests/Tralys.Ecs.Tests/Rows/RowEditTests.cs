namespace Tralys.Ecs.Tests;

public sealed partial class RowEditTests
{
    [Query]
    static void Tally(in Position position, World world, [Arg] Entity owner) =>
        world.TallyRows(owner).Set(new Tally { Slot = 0, Count = 1 });

    [Test]
    public async Task An_edit_of_a_waiting_row_write_refuses_the_write_once_a_playback_applies_it()
    {
        using var world = TestWorlds.Create();
        var owner = world.Create();
        var walker = world.Create();
        world.Set(walker, new Position());
        using (world.Batch(walker))
            RunTally(world, owner);

        await Assert.That(WriteAcrossPlayback(world, owner)).IsTrue();
    }

    static bool WriteAcrossPlayback(World world, Entity owner)
    {
        var edit = world.TallyRows(owner).Edit(0);
        return WriteAfterPlayback(world, ref edit);
    }

    static bool WriteAfterPlayback(World world, ref RowEdit<int, Tally> edit)
    {
        world.Playback();
        try
        {
            edit.Value.Count = 5;
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        finally
        {
            try
            {
                edit.Dispose();
            }
            catch (InvalidOperationException) { }
        }
    }

    [Test]
    public async Task Removing_an_earlier_row_while_an_edit_is_open_throws_and_keeps_both_rows()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.EffectRows(entity).Set(new Effect { Kind = 1, Stacks = 10 });
        world.EffectRows(entity).Set(new Effect { Kind = 2, Stacks = 20 });

        var threw = RemoveFirstWhileEditingSecond(world, entity);

        await Assert.That(threw).IsTrue();
        await Assert.That(world.EffectRows(entity).Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_presence_key_outside_the_mask_is_refused_before_a_row_is_added()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.SeatRows(entity).Set(new Seat { Slot = 3 });

        await Assert
            .That(() => world.SeatRows(entity).Set(new Seat { Slot = 64 }))
            .Throws<InvalidOperationException>();
        await Assert.That(world.SeatRows(entity).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Editing_a_missing_row_throws()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();

        await Assert.That(() => EditMissing(world, entity)).Throws<InvalidOperationException>();
    }

    static void EditMissing(World world, Entity entity)
    {
        using var edit = world.EffectRows(entity).Edit(1);
    }

    [Test]
    public async Task An_edit_left_open_across_a_reset_that_drops_its_rows_refuses_the_write()
    {
        using var world = TestWorlds.Create();
        var kept = world.Create();
        world.EffectRows(kept).Set(new Effect { Kind = 1 });
        var baseline = world.Snapshot();
        var dropped = world.Create();
        world.EffectRows(dropped).Set(new Effect { Kind = 2 });

        await Assert.That(WriteAcrossReset(world, dropped, baseline)).IsTrue();
    }

    static bool WriteAcrossReset(World world, Entity entity, WorldSnapshot baseline)
    {
        var edit = world.EffectRows(entity).Edit(2);
        return WriteAfterReset(world, baseline, ref edit);
    }

    static bool WriteAfterReset(World world, WorldSnapshot baseline, ref RowEdit<int, Effect> edit)
    {
        world.Reset(baseline);
        try
        {
            edit.Value.Stacks = 5;
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        finally
        {
            try
            {
                edit.Dispose();
            }
            catch (InvalidOperationException) { }
        }
    }

    static bool RemoveFirstWhileEditingSecond(World world, Entity entity)
    {
        var edit = world.EffectRows(entity).EditOrAdd(2, out _);
        return RemoveFirstWhileEditing(world, entity, ref edit);
    }

    static bool RemoveFirstWhileEditing(World world, Entity entity, ref RowEdit<int, Effect> edit)
    {
        try
        {
            world.EffectRows(entity).Remove(1);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        finally
        {
            edit.Dispose();
        }
    }
}
