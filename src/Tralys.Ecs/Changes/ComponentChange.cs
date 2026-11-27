namespace Tralys.Ecs;

// The one decision every write path makes: the log follows storage, observers follow presence (stored and enabled).
internal readonly struct ComponentChange
{
    public readonly bool LogRemoved;
    public readonly bool LogAdded;
    public readonly bool LogEnabled;
    public readonly bool LogDisabled;
    public readonly bool LogReplaced;
    public readonly bool HearRemove;
    public readonly bool HearAdd;
    public readonly bool HearReplace;
    public readonly bool StampAdded;

    ComponentChange(
        bool storedBefore,
        bool enabledBefore,
        bool storedAfter,
        bool enabledAfter,
        bool readded,
        bool replaced
    )
    {
        LogRemoved = storedBefore && (!storedAfter || readded);
        LogAdded = storedAfter && (!storedBefore || readded);
        var kept = storedBefore && storedAfter && !readded;
        LogEnabled = kept && !enabledBefore && enabledAfter;
        LogDisabled = (LogAdded && !enabledAfter) || (kept && enabledBefore && !enabledAfter);
        var presentBefore = storedBefore && enabledBefore;
        var presentAfter = storedAfter && enabledAfter;
        HearRemove = presentBefore && (!presentAfter || readded);
        HearAdd = presentAfter && (!presentBefore || readded);
        HearReplace = replaced && presentBefore && presentAfter && !readded;
        LogReplaced = HearReplace;
        StampAdded = LogAdded || HearAdd;
    }

    public static ComponentChange Of(
        bool storedBefore,
        bool enabledBefore,
        bool storedAfter,
        bool enabledAfter,
        bool readded = false,
        bool replaced = false
    ) => new(storedBefore, enabledBefore, storedAfter, enabledAfter, readded, replaced);

    public static readonly ComponentChange Added = Of(false, false, true, true);

    public static ComponentChange Toggled(bool enabled) => Of(true, !enabled, true, enabled);

    public static ComponentChange Removed(bool enabled) => Of(true, enabled, false, false);
}

public sealed partial class World
{
    internal void Report(
        in ComponentChange change,
        Entity entity,
        int type,
        LifecycleReason reason,
        ref byte old,
        ref byte value
    )
    {
        Log(change, entity, type, reason);
        if (change.HearRemove)
            observers.QueueRemove(entity, type, ref Bytes(ref old), reason);
        if (change.HearAdd)
            observers.QueueAdd(entity, type, ref Bytes(ref value), reason);
        if (change.HearReplace)
            observers.QueueReplace(entity, type, ref Bytes(ref old), ref Bytes(ref value));
    }

    internal void Log(in ComponentChange change, Entity entity, int type, LifecycleReason reason)
    {
        if (change.LogRemoved)
            changes.Append(LogKind.Removed, entity, type, reason);
        LogAfterRemoval(change, entity, type, reason);
    }

    internal void LogAfterRemoval(
        in ComponentChange change,
        Entity entity,
        int type,
        LifecycleReason reason
    )
    {
        if (change.LogAdded)
            changes.Append(LogKind.Added, entity, type, reason);
        if (change.LogEnabled)
            changes.Append(LogKind.Enabled, entity, type, reason);
        if (change.LogDisabled)
            changes.Append(LogKind.Disabled, entity, type, reason);
        if (change.LogReplaced)
            changes.Append(LogKind.Replaced, entity, type, reason);
    }

    internal void Stamp(bool added, Archetype archetype, int tracked, int row)
    {
        if (added)
            archetype.StampAdded(tracked, row, version);
        else
            archetype.Stamp(tracked, row, version);
    }

    internal void Stamp(bool added, SparseStorage storage, int at)
    {
        if (added)
            storage.StampAdded(at, version);
        else
            storage.Stamp(at, version);
    }

    static ref byte Bytes(ref byte value) =>
        ref System.Runtime.CompilerServices.Unsafe.IsNullRef(ref value)
            ? ref ZeroBytes.Value
            : ref value;
}
