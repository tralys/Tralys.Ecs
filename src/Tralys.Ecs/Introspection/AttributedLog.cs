using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>The source line a write was made from, taken from caller info.</summary>
public readonly record struct CallSite(string? File, int Line)
{
    /// <summary>False when no source line was recorded for the write.</summary>
    public bool IsKnown => File is not null;

    /// <inheritdoc/>
    public override string ToString() => File is null ? "unknown" : $"{File}:{Line}";
}

/// <summary>A structural-log entry with who made it: <see cref="Executing"/> was running when the entry was logged, <see cref="Origin"/>
/// made the write (for deferred work, the system that recorded the command), and <see cref="ChangedBy"/> is the line it was made from.</summary>
public readonly record struct AttributedLogEntry(
    LogEntry Entry,
    SystemId Executing,
    SystemId Origin,
    CallSite ChangedBy
);

internal struct WriteTag
{
    public SystemId Origin;
    public CallSite Site;
    public bool Present;
}

/// <summary>The newest structural-log entries of one world, each with its <see cref="AttributedLogEntry"/> attribution. Kept only when
/// <see cref="WorldOptions.AttributedLogCapacity"/> is set; the oldest entry drops out once the log is full.</summary>
public sealed class AttributedLog
{
    readonly AttributedLogEntry[] ring;
    int next;
    int count;

    internal CallSite site;
    WriteTag replay;
    bool replaying;
    WriteTag[] destroys = [];

    internal static readonly AttributedLog Empty = new(0);

    internal AttributedLog(int capacity) => ring = new AttributedLogEntry[capacity];

    /// <summary>Entries kept before the oldest drops out; 0 when <see cref="WorldOptions.AttributedLogCapacity"/> is unset.</summary>
    public int Capacity => ring.Length;

    /// <summary>Entries held, at most <see cref="Capacity"/>.</summary>
    public int Count => count;

    /// <summary>The entry at <paramref name="index"/>, oldest first.</summary>
    public AttributedLogEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(index));
            var at = next - count + index;
            return ring[at < 0 ? at + ring.Length : at];
        }
    }

    /// <summary>The newest entry of <paramref name="kind"/> for <paramref name="entity"/>, of component <paramref name="type"/> unless it
    /// is -1.</summary>
    public bool TryFindLast(
        Entity entity,
        LogKind kind,
        out AttributedLogEntry entry,
        int type = -1
    )
    {
        for (var i = count - 1; i >= 0; i--)
        {
            var candidate = this[i];
            var logged = candidate.Entry;
            if (
                logged.Entity == entity
                && logged.Kind == kind
                && (type < 0 || logged.TypeIndex == type)
            )
            {
                entry = candidate;
                return true;
            }
        }
        entry = default;
        return false;
    }

    internal void Add(in LogEntry entry, SystemId executing)
    {
        var tag = Current(executing);
        ring[next] = new AttributedLogEntry(entry, executing, tag.Origin, tag.Site);
        next = next + 1 == ring.Length ? 0 : next + 1;
        if (count < ring.Length)
            count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal WriteTag Current(SystemId running) =>
        replaying
            ? replay
            : new WriteTag
            {
                Origin = running,
                Site = site,
                Present = true,
            };

    internal void BeginReplay(in WriteTag tag)
    {
        replay = tag;
        replaying = true;
    }

    internal void EndReplay() => replaying = false;

    internal void TagDestroy(uint index, in WriteTag tag)
    {
        if (index >= destroys.Length)
            Array.Resize(ref destroys, Math.Max((int)index + 1, destroys.Length * 2));
        destroys[index] = tag;
    }

    internal void InheritDestroy(uint index, uint from) => TagDestroy(index, DestroyTag(from));

    internal WriteTag DestroyTag(uint index) => index < destroys.Length ? destroys[index] : default;

    internal WriteTag[] DestroyTags(List<Entity> pending)
    {
        var tags = new WriteTag[pending.Count];
        for (var i = 0; i < tags.Length; i++)
            tags[i] = DestroyTag(pending[i].Index);
        return tags;
    }

    internal void Clear()
    {
        Array.Clear(ring);
        next = count = 0;
        Array.Clear(destroys);
        replaying = false;
        site = default;
    }
}

public sealed partial class World
{
    internal readonly AttributedLog? attributed;

    /// <summary>The attributed log; unless <see cref="WorldOptions.AttributedLogCapacity"/> is set, a shared empty log of capacity 0 that
    /// records nothing.</summary>
    public AttributedLog AttributedLog => attributed ?? AttributedLog.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void At(string file, int line)
    {
        if (attributed is { } log)
            log.site = new CallSite(file, line);
    }
}
