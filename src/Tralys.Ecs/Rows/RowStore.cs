using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

internal abstract class RowStore(int type)
{
    public readonly int Type = type;

    public abstract void ApplyUpsert(
        World world,
        Entity entity,
        ref byte row,
        LifecycleReason reason
    );

    public abstract void ApplyRemove(World world, Entity entity, ref byte row);

    public abstract void ReleaseEntity(World world, Entity entity, LifecycleReason reason);

    public abstract void Clear();

    public abstract RowStore Clone();

    public abstract void Load(RowStore source);

    public abstract int LiveRows(Entity owner);

    public abstract void CopyRows(Entity owner, Array into, int at);

    // Counts removed slots too; SlotRef returns a null ref for those.
    public abstract int SlotCount(Entity owner);

    public abstract ref byte SlotRef(Entity owner, int slot);
}

internal sealed class RowStore<TKey, TRow> : RowStore
    where TKey : notnull
    where TRow : struct, IRow<TKey>
{
    const int IndexAbove = 8;

    internal sealed class Block(bool stamped)
    {
        public readonly bool Stamped = stamped;
        public Entity Owner;
        public TRow[] Rows = new TRow[4];
        public ulong[] Changed = stamped ? new ulong[4] : [];
        public ulong[] Added = stamped ? new ulong[4] : [];
        public bool[] Dead = new bool[4];
        public int Count;
        public int Live;
        public int Iterating;
        public int Editing;

        // Bumped whenever rows move, so an open edit can tell its ref went stale.
        public int Moves;
        public ulong Presence;
        public Dictionary<TKey, int>? Index;

        public int Slot;
        public ulong MaxChanged;
        public int Newer = -1;
        public int Older = -1;
        public bool Raised;

        // The row, dead and, when stamped, stamp arrays grow together, so they keep one length.
        public void Load(Block source)
        {
            Owner = source.Owner;
            Slot = source.Slot;
            MaxChanged = source.MaxChanged;
            Newer = source.Newer;
            Older = source.Older;
            Raised = source.Raised;
            if (Rows.Length < source.Count)
            {
                var length = source.Rows.Length;
                Rows = new TRow[length];
                if (Stamped)
                {
                    Changed = new ulong[length];
                    Added = new ulong[length];
                }
                Dead = new bool[length];
            }
            Copies.Load(ref Rows, source.Rows, source.Count);
            if (Stamped)
            {
                Copies.Load(ref Changed, source.Changed, source.Count);
                Copies.Load(ref Added, source.Added, source.Count);
            }
            Copies.Load(ref Dead, source.Dead, source.Count);
            Count = source.Count;
            Live = source.Live;
            Iterating = 0;
            Editing = 0;
            Moves = source.Moves;
            Presence = source.Presence;
            Copies.Load(ref Index, source.Index);
        }
    }

    static readonly EqualityComparer<TKey> Keys = EqualityComparer<TKey>.Default;

    readonly World world;
    readonly bool tracked;
    readonly bool presence;
    readonly bool ownsBuffers;
    readonly EntityMap map = new();
    Block[] blocks = new Block[8];
    int blockCount;
    readonly Stack<int> freeBlocks = new();
    readonly Dictionary<TKey, int>? owners;
    readonly ListArena<Entity> ownerLists = new();

    // Stamped blocks, newest first by MaxChanged as of the last Settle; blocks raised since wait in `raised`.
    int newest = -1;
    int[] raised = new int[8];
    ulong[] raisedOrder = new ulong[8];
    int raisedCount;
    bool walking;

    public RowStore(World world, int type)
        : base(type)
    {
        this.world = world;
        var descriptor = world.registry[type];
        tracked = descriptor.IsStamped;
        ownsBuffers = world.layout.OwnsBuffers[type];
        presence = (descriptor.Traits & ComponentTraits.PresenceMask) != 0;
        if (
            presence
            && (
                RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()
                || Unsafe.SizeOf<TKey>() is not (1 or 2 or 4 or 8)
            )
        )
            throw new NotSupportedException(
                $"{descriptor.Name}: a presence mask needs an integer or enum key."
            );
        if ((descriptor.Traits & ComponentTraits.ReverseKeyIndex) != 0)
            owners = [];
    }

    RowStore(RowStore<TKey, TRow> config)
        : base(config.Type)
    {
        world = config.world;
        tracked = config.tracked;
        presence = config.presence;
        ownsBuffers = config.ownsBuffers;
        if (config.owners is not null)
            owners = [];
    }

    public override RowStore Clone()
    {
        var copy = new RowStore<TKey, TRow>(this);
        copy.Load(this);
        return copy;
    }

    public override void Load(RowStore other)
    {
        var source = (RowStore<TKey, TRow>)other;
        map.Load(source.map);
        if (blocks.Length < source.blockCount)
            Array.Resize(ref blocks, source.blocks.Length);
        for (var i = 0; i < source.blockCount; i++)
            (blocks[i] ??= new Block(tracked)).Load(source.blocks[i]);
        Array.Clear(blocks, source.blockCount, blocks.Length - source.blockCount);
        blockCount = source.blockCount;
        Copies.Load(freeBlocks, source.freeBlocks);
        newest = source.newest;
        Copies.Load(ref raised, source.raised, source.raisedCount);
        raisedCount = source.raisedCount;
        if (owners is not null)
            Copies.Load(owners, source.owners!);
        ownerLists.Load(source.ownerLists);
    }

    public override int LiveRows(Entity owner) => BlockOf(owner)?.Live ?? 0;

    public override int SlotCount(Entity owner) => BlockOf(owner)?.Count ?? 0;

    public override ref byte SlotRef(Entity owner, int slot)
    {
        var block = BlockOf(owner)!;
        return ref block.Dead[slot]
            ? ref Unsafe.NullRef<byte>()
            : ref Unsafe.As<TRow, byte>(ref block.Rows[slot]);
    }

    public override void CopyRows(Entity owner, Array into, int at)
    {
        if (BlockOf(owner) is not { } block)
            return;
        var rows = Unsafe.As<TRow[]>(into);
        for (var i = 0; i < block.Count; i++)
        {
            if (!block.Dead[i])
                rows[at++] = block.Rows[i];
        }
    }

    public Block? BlockOf(Entity entity)
    {
        var slot = map.Get(entity.Index);
        if (slot < 0)
            return null;
        var block = blocks[slot];
        return block.Owner == entity ? block : null;
    }

    Block EnsureBlock(Entity entity)
    {
        if (BlockOf(entity) is { } existing)
            return existing;
        int slot;
        if (freeBlocks.Count > 0)
            slot = freeBlocks.Pop();
        else
        {
            slot = blockCount++;
            if (slot == blocks.Length)
                Array.Resize(ref blocks, blocks.Length * 2);
            blocks[slot] = new Block(tracked) { Slot = slot };
        }
        var block = blocks[slot];
        block.Owner = entity;
        map.Set(entity.Index, slot);
        return block;
    }

    public int Find(Block block, in TKey key)
    {
        if (presence && (!TryBit(key, out var bit) || (block.Presence & bit) == 0))
            return -1;
        if (block.Index is { } index)
            return index.TryGetValue(key, out var at) ? at : -1;
        for (var i = 0; i < block.Count; i++)
        {
            if (!block.Dead[i] && Keys.Equals(block.Rows[i].RowKey, key))
                return i;
        }
        return -1;
    }

    public int Upsert(
        Entity entity,
        in TKey key,
        out Block block,
        out bool added,
        LifecycleReason reason = default
    )
    {
        if (presence)
            _ = Bit(key);
        block = EnsureBlock(entity);
        var at = Find(block, key);
        added = at < 0;
        return added ? Insert(block, key, reason) : at;
    }

    int Insert(Block block, in TKey key, LifecycleReason reason)
    {
        CheckNotEditing(block);
        if (block.Count == block.Rows.Length)
            Grow(block);
        var at = block.Count++;
        block.Live++;
        ref var row = ref block.Rows[at];
        row = default;
        row.RowKey = key;
        block.Dead[at] = false;
        var stamp = tracked && world.Tracking ? world.version : 0;
        if (tracked)
            block.Changed[at] = block.Added[at] = stamp;
        Raise(block, stamp);
        if (block.Index is { } index)
            index[key] = at;
        else if (block.Live > IndexAbove)
            BuildIndex(block);
        if (presence)
            block.Presence |= Bit(key);
        if (owners is not null)
            AddOwner(key, block.Owner);
        world.changes.Append(LogKind.RowAdded, block.Owner, Type, reason);
        return at;
    }

    public void NotifyAdded(Block block, int at, LifecycleReason reason) =>
        world.observers.QueueAdd(
            block.Owner,
            Type,
            ref Unsafe.As<TRow, byte>(ref block.Rows[at]),
            reason
        );

    void CheckNotEditing(Block block)
    {
        if (block.Editing > 0)
            throw new InvalidOperationException(
                $"{block.Owner}'s {world.registry[Type].Name} rows cannot be added or removed while an edit of one of them is open; dispose the edit first."
            );
    }

    public void BeginEdit(Block block) => block.Editing++;

    public void EndEdit(Block block)
    {
        if (--block.Editing == 0 && block.Iterating == 0 && block.Live != block.Count)
            Compact(block);
    }

    public void Stamp(Block block, int at)
    {
        if (tracked && world.Tracking)
        {
            block.Changed[at] = world.version;
            Raise(block, world.version);
        }
    }

    void Raise(Block block, ulong stamp)
    {
        if (stamp <= block.MaxChanged)
            return;
        block.MaxChanged = stamp;
        if (block.Raised)
            return;
        block.Raised = true;
        if (raisedCount == raised.Length)
            Array.Resize(ref raised, raised.Length * 2);
        raised[raisedCount++] = block.Slot;
    }

    public Block At(int slot) => blocks[slot];

    // A settle moves raised blocks ahead of an open walk, which then never reaches them.
    public int BeginWalk()
    {
        if (walking)
            throw new InvalidOperationException(
                $"A ChangedRows walk of {world.registry[Type].Name} is already open; finish or dispose it before starting another."
            );
        walking = true;
        return Settle();
    }

    public void EndWalk() => walking = false;

    int Settle()
    {
        if (raisedCount == 0)
            return newest;
        if (raisedOrder.Length < raisedCount)
            raisedOrder = new ulong[raised.Length];
        for (var i = 0; i < raisedCount; i++)
            raisedOrder[i] = blocks[raised[i]].MaxChanged;
        Array.Sort(raisedOrder, raised, 0, raisedCount);
        for (var i = 0; i < raisedCount; i++)
        {
            var block = blocks[raised[i]];
            block.Raised = false;
            if (block.MaxChanged == 0)
                continue;
            Unlink(block);
            block.Older = newest;
            if (newest >= 0)
                blocks[newest].Newer = block.Slot;
            newest = block.Slot;
        }
        raisedCount = 0;
        return newest;
    }

    void Unlink(Block block)
    {
        if (block.Newer >= 0)
            blocks[block.Newer].Older = block.Older;
        else if (newest == block.Slot)
            newest = block.Older;
        else
            return;
        if (block.Older >= 0)
            blocks[block.Older].Newer = block.Newer;
        block.Newer = block.Older = -1;
    }

    public void Remove(Block block, int at, LifecycleReason reason)
    {
        CheckNotEditing(block);
        var departing = block.Rows[at];
        var key = departing.RowKey;
        block.Live--;
        if (presence)
            block.Presence &= ~Bit(key);
        block.Index?.Remove(key);
        if (owners is not null)
            RemoveOwner(key, block.Owner);
        if (block.Iterating > 0)
        {
            block.Dead[at] = true;
            block.Rows[at] = default;
        }
        else
            Shift(block, at);
        world.changes.Append(LogKind.RowRemoved, block.Owner, Type, reason);
        ref var bytes = ref Unsafe.As<TRow, byte>(ref departing);
        if (ownsBuffers)
            world.FreeBuffers(Type, ref bytes);
        world.observers.QueueRemove(block.Owner, Type, ref bytes, reason);
    }

    public void RemoveAll(Block block, LifecycleReason reason)
    {
        CheckNotEditing(block);
        for (var at = 0; at < block.Count; at++)
        {
            if (!block.Dead[at])
            {
                block.Iterating++;
                Remove(block, at, reason);
                block.Iterating--;
            }
        }
        if (block.Iterating == 0)
            Compact(block);
    }

    public void EndIteration(Block block)
    {
        if (--block.Iterating == 0 && block.Editing == 0 && block.Live != block.Count)
            Compact(block);
    }

    void Shift(Block block, int at)
    {
        var tail = block.Count - at - 1;
        Array.Copy(block.Rows, at + 1, block.Rows, at, tail);
        if (tracked)
        {
            Array.Copy(block.Changed, at + 1, block.Changed, at, tail);
            Array.Copy(block.Added, at + 1, block.Added, at, tail);
        }
        Array.Copy(block.Dead, at + 1, block.Dead, at, tail);
        block.Count--;
        block.Rows[block.Count] = default;
        block.Moves++;
        if (block.Index is not null)
            BuildIndex(block);
    }

    void Compact(Block block)
    {
        var to = 0;
        for (var from = 0; from < block.Count; from++)
        {
            if (block.Dead[from])
                continue;
            if (to != from)
            {
                block.Rows[to] = block.Rows[from];
                if (tracked)
                {
                    block.Changed[to] = block.Changed[from];
                    block.Added[to] = block.Added[from];
                }
                block.Dead[to] = false;
            }
            to++;
        }
        Array.Clear(block.Rows, to, block.Count - to);
        Array.Clear(block.Dead, to, block.Count - to);
        block.Count = to;
        block.Moves++;
        if (block.Index is not null)
            BuildIndex(block);
    }

    void Grow(Block block)
    {
        var size = block.Rows.Length * 2;
        Array.Resize(ref block.Rows, size);
        if (tracked)
        {
            Array.Resize(ref block.Changed, size);
            Array.Resize(ref block.Added, size);
        }
        Array.Resize(ref block.Dead, size);
        block.Moves++;
    }

    void BuildIndex(Block block)
    {
        var index = block.Index ??= new Dictionary<TKey, int>(Keys);
        index.Clear();
        for (var i = 0; i < block.Count; i++)
        {
            if (!block.Dead[i])
                index[block.Rows[i].RowKey] = i;
        }
    }

    static ulong Bit(in TKey key) =>
        TryBit(key, out var bit)
            ? bit
            : throw new InvalidOperationException($"Presence-mask key {key} is outside 0..63.");

    static bool TryBit(in TKey key, out ulong bit)
    {
        long value = Unsafe.SizeOf<TKey>() switch
        {
            1 => Unsafe.As<TKey, byte>(ref Unsafe.AsRef(in key)),
            2 => Unsafe.As<TKey, short>(ref Unsafe.AsRef(in key)),
            4 => Unsafe.As<TKey, int>(ref Unsafe.AsRef(in key)),
            _ => Unsafe.As<TKey, long>(ref Unsafe.AsRef(in key)),
        };
        bit = (ulong)value < 64 ? 1UL << (int)value : 0;
        return bit != 0;
    }

    void AddOwner(in TKey key, Entity entity)
    {
        if (!owners!.TryGetValue(key, out var list))
            owners[key] = list = ownerLists.New();
        ownerLists.Add(list, entity);
    }

    void RemoveOwner(in TKey key, Entity entity)
    {
        if (!owners!.TryGetValue(key, out var list))
            return;
        var at = ownerLists.Span(list).IndexOf(entity);
        if (at >= 0)
        {
            ownerLists.RemoveAt(list, at);
        }
        if (ownerLists.Count(list) == 0)
        {
            ownerLists.Free(list);
            owners.Remove(key);
        }
    }

    public EntityList ListOwners(in TKey key)
    {
        if (owners is null)
            throw new InvalidOperationException(
                $"{world.registry[Type].Name} has no reverse key index; declare it [Rows(ReverseKeyIndex = true)]."
            );
        return owners.TryGetValue(key, out var list) ? new EntityList(ownerLists, list) : default;
    }

    public override void ApplyUpsert(
        World world,
        Entity entity,
        ref byte row,
        LifecycleReason reason
    )
    {
        ref var incoming = ref Unsafe.As<byte, TRow>(ref row);
        var at = Upsert(entity, incoming.RowKey, out var block, out var added, reason);
        ref var stored = ref block.Rows[at];
        if (
            added
            || !ComponentOpsOf<TRow>.Equals(
                ref Unsafe.As<TRow, byte>(ref stored),
                ref Unsafe.As<TRow, byte>(ref incoming)
            )
        )
        {
            if (!added && ownsBuffers)
                world.FreeReplacedBuffers(
                    Type,
                    ref Unsafe.As<TRow, byte>(ref stored),
                    ref Unsafe.As<TRow, byte>(ref incoming)
                );
            stored = incoming;
            if (added)
                NotifyAdded(block, at, reason);
            else
                Stamp(block, at);
        }
    }

    public void Changed(Block block, int at, in TRow before)
    {
        if (ownsBuffers)
            world.FreeReplacedBuffers(
                Type,
                ref Unsafe.As<TRow, byte>(ref Unsafe.AsRef(in before)),
                ref Unsafe.As<TRow, byte>(ref block.Rows[at])
            );
        Stamp(block, at);
    }

    public override void ApplyRemove(World world, Entity entity, ref byte row)
    {
        var key = Unsafe.As<byte, TRow>(ref row).RowKey;
        if (BlockOf(entity) is { } block && Find(block, key) is var at and >= 0)
            Remove(block, at, default);
    }

    public override void ReleaseEntity(World world, Entity entity, LifecycleReason reason)
    {
        var slot = map.Get(entity.Index);
        if (slot < 0 || blocks[slot].Owner != entity)
            return;
        var block = blocks[slot];
        block.Editing = 0;
        RemoveAll(block, reason);
        Unlink(block);
        block.MaxChanged = 0;
        block.Owner = default;
        block.Presence = 0;
        block.Index = null;
        block.Iterating = 0;
        map.Remove(entity.Index);
        freeBlocks.Push(slot);
    }

    public override void Clear()
    {
        map.Clear();
        for (var i = 0; i < blockCount; i++)
        {
            var block = blocks[i];
            Array.Clear(block.Rows, 0, block.Count);
            block.Count = block.Live = block.Iterating = block.Editing = 0;
            block.Owner = default;
            block.Presence = 0;
            block.Index = null;
            block.MaxChanged = 0;
            block.Newer = block.Older = -1;
            block.Raised = false;
        }
        newest = -1;
        raisedCount = 0;
        freeBlocks.Clear();
        for (var i = blockCount - 1; i >= 0; i--)
            freeBlocks.Push(i);
        owners?.Clear();
        ownerLists.Clear();
    }
}
