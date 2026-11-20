namespace Tralys.Ecs.Tests;

public sealed class SparseRecycleTests
{
    [Test]
    public async Task A_recycled_slot_inherits_no_sparse_component()
    {
        using var world = TestWorlds.Create();
        var old = world.Create();
        world.Set(old, new Dialog { Page = 4 });
        world.Destroy(old);
        world.Playback();

        var reuser = world.Create();

        await Assert.That(reuser.Index).IsEqualTo(old.Index);
        await Assert.That(world.Has<Dialog>(reuser)).IsFalse();
    }

    [Test]
    public async Task A_cancelled_creation_logs_nothing_and_frees_a_clean_slot()
    {
        using var world = TestWorlds.Logged();
        var tail = new LogTail(world);
        Entity transient;
        using (world.BeginDelivery())
        {
            transient = world.Create();
            world.Set(transient, new Dialog { Page = 4 });
            world.Destroy(transient);
        }
        world.Playback();

        var log = new List<LogKind>();
        foreach (var entry in tail.Read())
        {
            if (entry.Entity == transient)
                log.Add(entry.Kind);
        }
        var reuser = world.Create();

        await Assert.That(log).IsEmpty();
        await Assert.That(reuser.Index).IsEqualTo(transient.Index);
        await Assert.That(world.Has<Dialog>(reuser)).IsFalse();
    }

    [Test]
    public async Task A_recycled_slot_inherits_no_sparse_component_added_by_playback()
    {
        using var world = TestWorlds.Create();
        var old = world.Create();
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Set(old, new Dialog { Page = 4 });
        world.Submit(buffer);
        world.Playback();
        world.Destroy(old);
        world.Playback();

        var reuser = world.Create();

        await Assert.That(reuser.Index).IsEqualTo(old.Index);
        await Assert.That(world.Has<Dialog>(reuser)).IsFalse();
    }
}
