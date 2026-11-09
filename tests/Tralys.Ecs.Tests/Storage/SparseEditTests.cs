namespace Tralys.Ecs.Tests;

public sealed class SparseEditTests
{
    [Test]
    public async Task An_edit_open_across_a_sparse_removal_throws_on_dispose()
    {
        using var world = TestWorlds.Create();
        var first = world.Create();
        var second = world.Create();
        world.Set(first, new Dialog { Page = 1 });
        world.Set(second, new Dialog { Page = 2 });

        await Assert.That(WriteThroughRemovedEdit(world, first)).IsTrue();
    }

    static bool WriteThroughRemovedEdit(World world, Entity entity)
    {
        var edit = world.Edit<Dialog>(entity);
        world.Remove<Dialog>(entity);
        edit.Value.Page = 99;
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
}
