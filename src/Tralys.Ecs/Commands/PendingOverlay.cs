namespace Tralys.Ecs;

internal sealed class PendingOverlay
{
    // A pending enable flip of a stored component: the value stays in storage, only the bit is overlaid.
    public const int Stored = -2;

    // Entry types below zero mark an entity's recorded lifecycle rather than a component.
    public const int Destroying = -1;
    public const int Detaching = -3;

    struct Entry
    {
        public int Type;
        public bool Present;
        public bool Enabled;
        public int Payload;
        public int Next;
    }

    readonly Dictionary<uint, int> heads = [];
    Entry[] entries = new Entry[32];
    int count;

    public bool IsEmpty => heads.Count == 0;

    public bool HasLifecycle { get; private set; }

    int IndexOf(uint entity, int type, out int head)
    {
        head = heads.TryGetValue(entity, out var first) ? first : -1;
        for (var at = head; at >= 0; at = entries[at].Next)
        {
            if (entries[at].Type == type)
                return at;
        }
        return -1;
    }

    public bool TryGet(uint entity, int type, out bool present, out bool enabled, out int payload)
    {
        var at = IndexOf(entity, type, out _);
        if (at < 0)
        {
            present = enabled = false;
            payload = -1;
            return false;
        }
        present = entries[at].Present;
        enabled = present && entries[at].Enabled;
        payload = entries[at].Payload;
        return true;
    }

    public void Set(uint entity, int type, bool present, int payload)
    {
        var at = IndexOf(entity, type, out var head);
        if (at < 0)
        {
            if (count == entries.Length)
                Array.Resize(ref entries, count * 2);
            at = count++;
            entries[at] = new Entry { Type = type, Next = head };
            heads[entity] = at;
        }
        if (!entries[at].Present || !present)
            entries[at].Enabled = present;
        entries[at].Present = present;
        entries[at].Payload = payload;
    }

    public void Mark(uint entity, int lifecycle, bool on)
    {
        Set(entity, lifecycle, on, -1);
        HasLifecycle = true;
    }

    public bool Marked(uint entity, int lifecycle, bool unmarked) =>
        IndexOf(entity, lifecycle, out _) is var at and >= 0 ? entries[at].Present : unmarked;

    public IEnumerable<uint> Adding(int type)
    {
        foreach (var (entity, head) in heads)
        {
            for (var at = head; at >= 0; at = entries[at].Next)
            {
                if (entries[at].Type == type && entries[at].Present)
                    yield return entity;
            }
        }
    }

    public void SetEnabled(uint entity, int type, bool enabled)
    {
        var at = IndexOf(entity, type, out _);
        if (at >= 0)
            entries[at].Enabled = enabled;
    }

    // A flip with no entry yet leaves the value in storage and overlays only the bit.
    public void Flip(uint entity, int type, bool enabled)
    {
        if (IndexOf(entity, type, out _) < 0)
            Set(entity, type, true, Stored);
        SetEnabled(entity, type, enabled);
    }

    public void Drop(uint entity)
    {
        heads.Remove(entity);
        // A closed batch leaves nothing staged, so no playback would ever Clear the entries it wrote.
        if (heads.Count == 0)
            Clear();
    }

    public void Clear()
    {
        heads.Clear();
        count = 0;
        HasLifecycle = false;
    }
}
