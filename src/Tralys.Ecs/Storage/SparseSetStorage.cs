using System.Numerics;
using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

internal abstract class SparseStorage
{
    protected const int PageBits = 10;
    protected const int PageSize = 1 << PageBits;

    protected int[]?[] pages = new int[]?[4];
    public Entity[] Owners = new Entity[16];
    public ulong[] Changed = new ulong[16];
    public ulong[] Added = new ulong[16];
    public int Count;
    public int Moves;
    public readonly int Type;

    internal World? propagator;

    protected SparseStorage(int type) => Type = type;

    public abstract SparseStorage Clone();

    // The owner, stamp and value arrays grow together, so they keep one length.
    public void Load(SparseStorage source)
    {
        Copies.LoadPages(ref pages, source.pages, PageSize);
        if (Owners.Length < source.Count)
        {
            var length = source.Owners.Length;
            Owners = new Entity[length];
            Changed = new ulong[length];
            Added = new ulong[length];
            GrowValues(length);
        }
        Copies.Load(ref Owners, source.Owners, source.Count);
        Copies.Load(ref Changed, source.Changed, source.Count);
        Copies.Load(ref Added, source.Added, source.Count);
        LoadValues(source);
        Count = source.Count;
        Moves++;
    }

    protected abstract void LoadValues(SparseStorage source);

    public void Stamp(int slot, ulong version)
    {
        Changed[slot] = version;
        propagator?.Propagate(Owners[slot], Type, version);
    }

    public void StampAdded(int slot, ulong version)
    {
        Added[slot] = Changed[slot] = version;
        propagator?.Propagate(Owners[slot], Type, version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SlotOf(uint index)
    {
        var page = (int)(index >> PageBits);
        if ((uint)page >= (uint)pages.Length || pages[page] is not { } slots)
            return -1;
        return slots[index & (PageSize - 1)] - 1;
    }

    public bool Contains(uint index) => SlotOf(index) >= 0;

    protected int Insert(Entity entity)
    {
        var page = (int)(entity.Index >> PageBits);
        if (page >= pages.Length)
            Array.Resize(ref pages, Math.Max(page + 1, pages.Length * 2));
        var slots = pages[page] ??= new int[PageSize];
        if (Count == Owners.Length)
        {
            Array.Resize(ref Owners, Count * 2);
            Array.Resize(ref Changed, Count * 2);
            Array.Resize(ref Added, Count * 2);
            GrowValues(Count * 2);
        }
        var slot = Count++;
        Moves++;
        Owners[slot] = entity;
        slots[entity.Index & (PageSize - 1)] = slot + 1;
        return slot;
    }

    public bool Remove(uint index)
    {
        var slot = SlotOf(index);
        if (slot < 0)
            return false;
        var last = Count - 1;
        if (slot != last)
        {
            MoveValue(last, slot);
            Owners[slot] = Owners[last];
            Changed[slot] = Changed[last];
            Added[slot] = Added[last];
            pages[Owners[slot].Index >> PageBits]![Owners[slot].Index & (PageSize - 1)] = slot + 1;
        }
        ClearValue(last);
        Owners[last] = default;
        Changed[last] = 0;
        Added[last] = 0;
        pages[index >> PageBits]![index & (PageSize - 1)] = 0;
        Count--;
        Moves++;
        return true;
    }

    public void Clear()
    {
        for (var i = 0; i < Count; i++)
        {
            pages[Owners[i].Index >> PageBits]![Owners[i].Index & (PageSize - 1)] = 0;
            ClearValue(i);
            Owners[i] = default;
            Changed[i] = 0;
            Added[i] = 0;
        }
        Count = 0;
        Moves++;
    }

    public abstract ref byte RowRef(int slot);

    public abstract int AddFrom(Entity entity, ref byte value);

    public abstract void WriteFrom(int slot, ref byte value);

    protected abstract void GrowValues(int capacity);

    protected abstract void MoveValue(int from, int to);

    protected abstract void ClearValue(int slot);
}

internal sealed class SparseStorage<T> : SparseStorage
    where T : struct
{
    public T[] Values = new T[16];

    public SparseStorage(int type)
        : base(type) { }

    public override SparseStorage Clone()
    {
        var copy = new SparseStorage<T>(Type) { propagator = propagator };
        copy.Load(this);
        return copy;
    }

    protected override void LoadValues(SparseStorage source) =>
        Copies.Load(ref Values, ((SparseStorage<T>)source).Values, source.Count);

    public ref T Add(Entity entity, in T value, out int slot)
    {
        slot = Insert(entity);
        Values[slot] = value;
        return ref Values[slot];
    }

    public override ref byte RowRef(int slot) => ref Unsafe.As<T, byte>(ref Values[slot]);

    public override int AddFrom(Entity entity, ref byte value)
    {
        Add(entity, Unsafe.As<byte, T>(ref value), out var slot);
        return slot;
    }

    public override void WriteFrom(int slot, ref byte value) =>
        Values[slot] = Unsafe.As<byte, T>(ref value);

    protected override void GrowValues(int capacity) => Array.Resize(ref Values, capacity);

    protected override void MoveValue(int from, int to) => Values[to] = Values[from];

    protected override void ClearValue(int slot) => Values[slot] = default;
}

internal sealed class SparseTagStorage(int type)
{
    public readonly int Type = type;
    ulong[] bits = new ulong[16];

    public bool Contains(uint index)
    {
        var word = (int)(index >> 6);
        return word < bits.Length && (bits[word] & (1UL << (int)index)) != 0;
    }

    public bool Set(uint index, bool present)
    {
        var word = (int)(index >> 6);
        if (word >= bits.Length)
        {
            if (!present)
                return false;
            Array.Resize(ref bits, Math.Max(word + 1, bits.Length * 2));
        }
        var mask = 1UL << (int)index;
        var had = (bits[word] & mask) != 0;
        bits[word] = present ? bits[word] | mask : bits[word] & ~mask;
        return had != present;
    }

    public IEnumerable<uint> Indices()
    {
        for (var word = 0; word < bits.Length; word++)
        {
            for (var rest = bits[word]; rest != 0; rest &= rest - 1)
                yield return (uint)((word << 6) + BitOperations.TrailingZeroCount(rest));
        }
    }

    public void Clear() => Array.Clear(bits);

    public SparseTagStorage Clone()
    {
        var copy = new SparseTagStorage(Type);
        copy.Load(this);
        return copy;
    }

    public void Load(SparseTagStorage source) =>
        Copies.Load(ref bits, source.bits, source.bits.Length);
}
