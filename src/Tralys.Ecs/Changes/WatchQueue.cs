namespace Tralys.Ecs;

internal readonly record struct WatchEvent(Entity Entity, int Type, LogKind Kind);

internal sealed class WatchQueue(int slot, LogKinds[] kinds, bool takesRaises, int cap)
{
    public static readonly LogKinds Resyncs = LogKinds.Of(
        LogKind.Reattached,
        LogKind.DestroyCancelled,
        LogKind.Restored
    );

    public static readonly LogKinds Forgets = LogKinds.Of(LogKind.Destroyed, LogKind.Extracted);

    public static readonly LogKinds EntityKinds = Resyncs | Forgets;

    public readonly int Slot = slot;
    public readonly LogKinds[] Kinds = kinds;
    public readonly bool TakesRaises = takesRaises;
    public readonly List<WatchEvent> Events = [];
    public readonly List<Entity> Raised = [];
    readonly Lock raising = new();

    public bool Overflowed { get; private set; }

    public bool Wants(int type, LogKind kind) =>
        type < 0 ? EntityKinds.Has(kind) : type < Kinds.Length && Kinds[type].Has(kind);

    public void Push(in WatchEvent change)
    {
        if (Overflowed)
            return;
        if (Events.Count + Raised.Count >= cap)
            Overflow();
        else
            Events.Add(change);
    }

    // Parallel chunk writes to a propagating type raise ancestors concurrently.
    public void PushRaised(Entity ancestor)
    {
        lock (raising)
        {
            if (Overflowed)
                return;
            if (Events.Count + Raised.Count >= cap)
                Overflow();
            else
                Raised.Add(ancestor);
        }
    }

    public void Overflow()
    {
        Overflowed = true;
        Events.Clear();
        Raised.Clear();
    }

    public void Clear()
    {
        Overflowed = false;
        Events.Clear();
        Raised.Clear();
    }
}
