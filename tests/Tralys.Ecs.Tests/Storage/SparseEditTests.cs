namespace Tralys.Ecs.Tests;

public sealed class SparseEditTests
{
    [Test]
    public async Task An_edit_open_across_a_sparse_removal_throws_on_access_and_on_dispose()
    {
        using var world = TestWorlds.Create();
        var first = world.Create();
        var second = world.Create();
        world.Set(first, new Dialog { Page = 1 });
        world.Set(second, new Dialog { Page = 2 });

        await Assert.That(WriteThroughRemovedEdit(world, first)).IsEqualTo((true, true));
    }

    static (bool Write, bool Dispose) WriteThroughRemovedEdit(World world, Entity entity)
    {
        var edit = world.Edit<Dialog>(entity);
        return WriteAfterRemoval(world, entity, ref edit);
    }

    static (bool Write, bool Dispose) WriteAfterRemoval(
        World world,
        Entity entity,
        ref ComponentEdit<Dialog> edit
    )
    {
        world.Remove<Dialog>(entity);
        bool write;
        try
        {
            edit.Value.Page = 99;
            write = false;
        }
        catch (InvalidOperationException)
        {
            write = true;
        }
        try
        {
            edit.Dispose();
            return (write, false);
        }
        catch (InvalidOperationException)
        {
            return (write, true);
        }
    }
}
