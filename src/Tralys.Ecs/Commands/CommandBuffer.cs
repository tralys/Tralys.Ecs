using System.Diagnostics.CodeAnalysis;
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
    public Entity OldTarget;
    public int Type;
    public int Payload;
    public int SchedulePosition;
    public int ChunkIndex;
    public int Sequence;
    public int Buffer;
    public LifecycleReason Reason;
    public long Position;
    public CommandKind Kind;
    public bool Untracked;

    // A tracked write changed the value of an untracked Set in place, so playback stamps it as changed.
    public bool TrackedEdit;

    public readonly int Operands => Kind == CommandKind.Retarget ? 3 : 2;

    [UnscopedRef]
    public ref Entity Operand(int index)
    {
        if (index == 0)
            return ref Entity;
        if (index == 1)
            return ref Other;
        return ref OldTarget;
    }
}

/// <summary>Deferred structural commands from one writer: the serial stage, or one chunk work item. Single-writer, so lock-free.</summary>
internal sealed class CommandBuffer
{
    internal readonly World world;
    internal readonly bool parallel;
    readonly AttributedLog? log;
    internal int id;
    internal CommandRecord[] records = new CommandRecord[16];
    internal int count;
    int untracked;
    internal int schedulePosition;
    internal int chunkIndex;
    int sequence;
    internal int placeholders;
    internal uint rental;
    readonly PayloadStore?[] payloads;
    readonly List<PayloadStore> usedPayloads = [];
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
        log = world.attributed;
        payloads = new PayloadStore?[world.registry.IndexBound];
    }

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

    internal void Record(
        CommandKind kind,
        Entity entity,
        int type = -1,
        int payload = -1,
        LifecycleReason reason = default,
        Entity other = default,
        Entity oldTarget = default
    )
    {
        if (count == records.Length)
            Array.Resize(ref records, count * 2);
        if (log is not null)
            Tag(log);
        records[count++] = new CommandRecord
        {
            Kind = kind,
            Entity = entity,
            Other = other,
            OldTarget = oldTarget,
            Type = type,
            Payload = payload,
            Reason = reason,
            SchedulePosition = parallel ? schedulePosition : world.runSequence,
            ChunkIndex = parallel ? chunkIndex : -1,
            Sequence = sequence++,
            Buffer = id,
            Position = parallel ? 0 : world.IndexTick(),
            Untracked = !world.Tracking,
        };
        if (!world.Tracking)
            untracked++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Tag(AttributedLog log)
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
            : log.Current(world.currentSystem);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Effect(Effects.Structural)]
    internal void At(string file, int line)
    {
        if (log is not null)
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
        usedPayloads.Add(created);
        return created;
    }

    internal PayloadStore Payload(int type) => payloads[type] ?? ThrowNoPayloads(type);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    PayloadStore ThrowNoPayloads(int type) =>
        throw new InvalidOperationException(
            $"{world.registry[type].Name} has no payload store, yet a record holds a payload of it."
        );

    internal Entity NewPlaceholder()
    {
        var placeholder = Entity.Placeholder(rental, placeholders++);
        Record(CommandKind.Create, placeholder);
        return placeholder;
    }

    internal void RecordSet<T>(Entity entity, int type, in T value)
        where T : struct => Record(CommandKind.Set, entity, type, PayloadFor(type, value));

    internal int PayloadFor<T>(int type, in T value)
        where T : struct =>
        world.layout.Class[type] is StorageClass.Column or StorageClass.Sparse
            ? Payloads<T>(type).Add(value)
            : -1;

    internal void EditedTracked(Entity entity, int type)
    {
        if (
            untracked > 0
            && Latest(entity, type, CommandKind.Set, CommandKind.Set, count) is var at and >= 0
        )
            records[at].TrackedEdit = true;
    }

    internal int RecordValue<T>(CommandKind kind, Entity entity, in T value, Entity other = default)
        where T : struct
    {
        var type = world.registry.IndexOf<T>();
        var payload = Payloads<T>(type).Add(value);
        Record(kind, entity, type, payload, other: other);
        return payload;
    }

    internal int Latest(Entity entity, int type, CommandKind kind, CommandKind or, int before)
    {
        for (var i = Math.Min(before, count) - 1; i >= 0; i--)
        {
            ref readonly var record = ref records[i];
            if (
                record.Entity == entity
                && record.Type == type
                && (record.Kind == kind || record.Kind == or)
            )
                return i;
        }
        return -1;
    }

    internal int Next(Entity entity, int type, CommandKind kind, int from, int until)
    {
        for (var i = from; i < until; i++)
        {
            ref readonly var record = ref records[i];
            if (record.Entity == entity && record.Type == type && record.Kind == kind)
                return i;
        }
        return -1;
    }

    internal CommandKind KindOf(int record) => records[record].Kind;

    internal ref T PayloadOf<T>(int record)
        where T : struct =>
        ref Unsafe.As<PayloadStore<T>>(Payload(records[record].Type))[records[record].Payload];

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
        untracked = 0;
        for (var i = 0; i < count; i++)
        {
            if (records[i].Entity == entity)
                continue;
            if (records[i].Untracked)
                untracked++;
            if (kept != i)
            {
                records[kept] = records[i];
                // A record's Sequence is also its slot in tags.
                records[kept].Sequence = kept;
                if (log is not null)
                    tags[kept] = tags[i];
            }
            kept++;
        }
        count = sequence = kept;
        // Payloads are named only by records, and a playback with nothing to apply never clears them.
        if (count == 0)
        {
            foreach (var store in usedPayloads)
                store.Reset();
        }
    }

    internal void Clear()
    {
        indexEditCount = 0;
        count = 0;
        untracked = 0;
        sequence = 0;
        placeholders = 0;
        foreach (var store in usedPayloads)
            store.Reset();
    }

    internal bool IsEmpty => count == 0;
}
