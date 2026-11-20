namespace Tralys.Ecs.Tests;

sealed class LogTail(World world)
{
    int from = world.AttributedLog.Count;

    public List<LogEntry> Read()
    {
        var log = world.AttributedLog;
        if (log.Capacity == 0)
            throw new InvalidOperationException(
                "The world keeps no attributed log; create it with TestWorlds.Logged."
            );
        if (log.Count == log.Capacity)
            throw new InvalidOperationException(
                "The attributed log filled up; raise its capacity."
            );
        var entries = new List<LogEntry>();
        for (var i = from; i < log.Count; i++)
            entries.Add(log[i].Entry);
        from = log.Count;
        return entries;
    }
}
