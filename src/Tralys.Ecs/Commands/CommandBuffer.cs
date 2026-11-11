using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

internal enum CommandKind : byte
{
    Create,
    Destroy,
    Set,
    Remove,
    Enable,
    Disable,
    Detach,
    Reattach,
    CancelDestroy,
    Relate,
    Unrelate,
    SetParent,
    UpsertRow,
    RemoveRow,
    Retarget,
    ClearTargets,
    ClearSources,
}

internal struct CommandRecord
{
    public Entity Entity;
    public Entity Other;
    public int Type;
    public int Payload;
    public int SchedulePosition;
    public int ChunkIndex;
    public int Sequence;
    public int Buffer;
    public LifecycleReason Reason;
    public CommandKind Kind;
    public bool Untracked;
}

/// <summary>Deferred structural commands from one writer: the serial stage, or one chunk work item. Single-writer, so lock-free.</summary>
internal sealed class CommandBuffer
{
    internal readonly World world;
    internal readonly bool parallel;
    readonly bool attributing;
    internal int id;
    internal CommandRecord[] records = new CommandRecord[16];
    internal int count;
    internal int schedulePosition;
    internal int chunkIndex;
    int sequence;
    internal int placeholders;
    internal uint rental;
    readonly PayloadStore?[] payloads;
    readonly List<int> usedPayloads = [];
    internal IndexEdit[] indexEdits = [];
    internal int indexEditCount;
    internal WriteTag[] tags = [];
    internal CallSite site;
    internal long chunksVisited;
    internal long chunksSkipped;

    internal CommandBuffer(World world, bool parallel)
    {
        this.world = world;
        this.parallel = parallel;
        attributing = world.attributed is not null;
        payloads = new PayloadStore?[world.registry.IndexBound];
    }

    internal readonly List<Entity> retargets = [];

    public Commands Commands => new(this);

    internal void CheckLive(Entity entity)
    {
        if (entity.IsPlaceholder)
        {
            if (entity.PlaceholderRental != rental)
                throw new InvalidOperationException(
                    $"{entity} is a placeholder from another command buffer; a placeholder is only valid inside its own."
                );
            return;
        }
        if (!world.allocator.IsCurrent(entity))
            World.ThrowNotAlive(entity);
    }

    internal int Retarget(Entity oldTarget)
    {
        retargets.Add(oldTarget);
        return retargets.Count - 1;
    }

    internal void Record(
        CommandKind kind,
        Entity entity,
        int type = -1,
        int payload = -1,
        LifecycleReason reason = default,
        Entity other = default
    )
    {
        if (count == records.Length)
            Array.Resize(ref records, count * 2);
        if (attributing)
            Tag();
        records[count++] = new CommandRecord
        {
            Kind = kind,
            Entity = entity,
            Other = other,
            Type = type,
            Payload = payload,
            Reason = reason,
            SchedulePosition = parallel ? schedulePosition : world.runSequence,
            ChunkIndex = parallel ? chunkIndex : -1,
            Sequence = sequence++,
            Buffer = id,
            Untracked = !world.Tracking,
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Tag()
    {
        if (tags.Length < records.Length)
            Array.Resize(ref tags, records.Length);
        tags[count] = parallel
            ? new WriteTag
            {
                Origin = world.currentSystem,
                Site = site,
                Present = true,
            }
            : world.CurrentTag();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void At(string file, int line)
    {
        if (attributing)
            site = new CallSite(file, line);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PayloadStore<T> Payloads<T>(int type)
        where T : struct
    {
        if (payloads[type] is { } existing)
            return Unsafe.As<PayloadStore<T>>(existing);
        var created = new PayloadStore<T>();
        payloads[type] = created;
        usedPayloads.Add(type);
        return created;
    }

    internal PayloadStore Payload(int type) => payloads[type]!;

    internal Entity NewPlaceholder()
    {
        var placeholder = Entity.Placeholder(rental, placeholders++);
        Record(CommandKind.Create, placeholder);
        return placeholder;
    }

    internal void RecordSet<T>(Entity entity, in T value)
        where T : struct
    {
        var type = world.registry.IndexOf<T>();
        Record(CommandKind.Set, entity, type, Payloads<T>(type).Add(value));
    }

    internal void RecordValue<T>(
        CommandKind kind,
        Entity entity,
        in T value,
        Entity other = default
    )
        where T : struct
    {
        var type = world.registry.IndexOf<T>();
        Record(kind, entity, type, Payloads<T>(type).Add(value), other: other);
    }

    internal void RecordIndexEdit(Entity entity, int type)
    {
        if (indexEditCount == indexEdits.Length)
            Array.Resize(ref indexEdits, Math.Max(16, indexEditCount * 2));
        indexEdits[indexEditCount] = new IndexEdit
        {
            Entity = entity,
            Type = type,
            ChunkIndex = chunkIndex,
            Sequence = indexEditCount,
        };
        indexEditCount++;
    }

    internal void Drop(Entity entity)
    {
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            if (records[i].Entity == entity)
                continue;
            if (kept != i)
            {
                records[kept] = records[i];
                // A record's Sequence is also its slot in tags.
                records[kept].Sequence = kept;
                if (attributing)
                    tags[kept] = tags[i];
            }
            kept++;
        }
        count = sequence = kept;
        // Payloads are named only by records, and a playback with nothing to apply never clears them.
        if (count == 0)
        {
            foreach (var type in usedPayloads)
                payloads[type]!.Reset();
        }
    }

    internal void Clear()
    {
        indexEditCount = 0;
        count = 0;
        sequence = 0;
        placeholders = 0;
        retargets.Clear();
        foreach (var type in usedPayloads)
            payloads[type]!.Reset();
    }

    internal bool IsEmpty => count == 0;
}
