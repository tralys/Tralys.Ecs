namespace Tralys.Ecs.Tests;

public sealed class RowEditTests
{
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

    static bool RemoveFirstWhileEditingSecond(World world, Entity entity)
    {
        using var edit = world.EffectRows(entity).EditOrAdd(2, out _);
        try
        {
            world.EffectRows(entity).Remove(1);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
