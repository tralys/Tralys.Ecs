namespace Tralys.Ecs.Tests;

public sealed partial class RefWriteReindexTests
{
    [Query]
    static void Renumber(ref Badge badge, [Arg] int current, [Arg] int next)
    {
        if (badge.Number == current)
            badge.Number = next;
    }

    [Test]
    public async Task A_key_written_through_a_query_ref_moves_its_entry_under_the_conflict_policy()
    {
        using var world = TestWorlds.Create();
        var first = world.Create();
        world.Set(first, new Badge { Number = 1 });
        var second = world.Create();
        world.Set(second, new Badge { Number = 2 });

        RunRenumber(world, 1, 3);
        world.BadgeByNumber().TryGet(3, out var moved);
        var vacated = !world.BadgeByNumber().Contains(1);
        RunRenumber(world, 3, 2);
        world.BadgeByNumber().TryGet(2, out var holder);

        await Assert.That(moved).IsEqualTo(first);
        await Assert.That(vacated).IsTrue();
        await Assert.That(holder).IsEqualTo(first);
    }

    [Test]
    public async Task A_key_written_through_a_query_ref_in_an_untracked_scope_still_moves_its_entry()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Badge { Number = 1 });

        using (world.Untracked())
            RunRenumber(world, 1, 4);
        world.BadgeByNumber().TryGet(4, out var holder);

        await Assert.That(holder).IsEqualTo(entity);
        await Assert.That(world.BadgeByNumber().Contains(1)).IsFalse();
    }

    [Test]
    public async Task An_edit_spanning_another_write_of_its_key_indexes_its_final_value()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Badge { Number = 1 });

        using (var edit = world.Edit<Badge>(entity))
        {
            world.Set(entity, new Badge { Number = 5 });
            edit.Value.Number = 1;
        }
        world.BadgeByNumber().TryGet(1, out var holder);

        await Assert.That((holder, world.BadgeByNumber().Contains(5))).IsEqualTo((entity, false));
    }
}
