namespace Tralys.Ecs.Tests;

public sealed class CursorExpiryTests
{
    [Test]
    public async Task A_cursor_the_log_was_trimmed_past_reads_as_expired()
    {
        using var world = TestWorlds.Create(new WorldOptions { LogRetentionCap = 8 });
        var stalled = world.Changes.Register("stalled");
        for (var i = 0; i < 20; i++)
            world.Create();

        var status = world.Changes.Sample(stalled).Status;

        await Assert.That(status).IsEqualTo(CursorStatus.Expired);
    }
}
