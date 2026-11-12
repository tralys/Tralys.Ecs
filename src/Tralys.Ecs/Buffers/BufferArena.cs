using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tralys.Ecs;

/// <summary>A generation-checked handle to a variable-length buffer in a world's <see cref="BufferArena{T}"/>. A component, row or relation
/// field of this type owns its buffer: removing the value or destroying the entity frees it, and replacing the value frees it unless the new
/// value still holds the handle in any field, so a write may move it between fields. A buffer has one owner: two stored values holding one
/// handle are not supported, since the first released frees it and leaves the other's handle stale.</summary>
public readonly struct BufferHandle<T> : IEquatable<BufferHandle<T>>, IOwnedBuffer
    where T : unmanaged
{
    internal readonly int Slot;
    internal readonly uint Generation;

    internal BufferHandle(int slot, uint generation)
    {
        Slot = slot;
        Generation = generation;
    }

    /// <summary>The handle is <c>default</c> and names no buffer.</summary>
    public bool IsNull => Slot == 0;

    static void IOwnedBuffer.Free(World world, ref byte handle) =>
        world.FreeOwned(Unsafe.As<byte, BufferHandle<T>>(ref handle));

    static byte[]? IOwnedBuffer.Copy(World world, ref byte handle) =>
        world.Buffers<T>().TryGet(Unsafe.As<byte, BufferHandle<T>>(ref handle), out var span)
            ? MemoryMarshal.AsBytes(span).ToArray()
            : null;

    static int IOwnedBuffer.ElementSize => Unsafe.SizeOf<T>();

    static void IOwnedBuffer.Allocate(World world, ReadOnlySpan<byte> contents, ref byte handle)
    {
        var arena = world.Buffers<T>();
        var allocated = arena.Allocate(contents.Length / Unsafe.SizeOf<T>());
        contents.CopyTo(MemoryMarshal.AsBytes(arena.Get(allocated)));
        Unsafe.As<byte, BufferHandle<T>>(ref handle) = allocated;
    }

    /// <summary>The same slot and generation; contents are not compared.</summary>
    public bool Equals(BufferHandle<T> other) =>
        Slot == other.Slot && Generation == other.Generation;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BufferHandle<T> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Slot, Generation);

    /// <summary>The same slot and generation; contents are not compared.</summary>
    public static bool operator ==(BufferHandle<T> left, BufferHandle<T> right) =>
        left.Equals(right);

    /// <summary>A different slot or generation.</summary>
    public static bool operator !=(BufferHandle<T> left, BufferHandle<T> right) =>
        !left.Equals(right);
}

/// <summary>Per-world storage for variable-length buffers of <typeparamref name="T"/>. Freed slots bump their generation, so a stale handle
/// never reaches the slot's next buffer. Arrays are pooled by power-of-two size.</summary>
public sealed class BufferArena<T> : IBufferArena
    where T : unmanaged
{
    struct Entry
    {
        public T[]? Array;
        public int Length;
        public uint Generation;
    }

    Entry[] entries = new Entry[16];
    int count = 1;
    readonly Stack<int> free = new();
    readonly Stack<T[]>?[] pools = new Stack<T[]>?[32];
    readonly World world;

    internal BufferArena(World world) => this.world = world;

    /// <summary>How many buffers are allocated and not yet freed.</summary>
    public int LiveCount => count - 1 - free.Count;

    /// <summary>A new zeroed buffer of <paramref name="length"/> elements. Store the handle in a component to tie the buffer's life to
    /// it; otherwise <see cref="Free"/> it.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    [Effect(Effects.Mutates)]
    public BufferHandle<T> Allocate(int length)
    {
        world.CheckWritable();
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        int slot;
        if (free.Count > 0)
            slot = free.Pop();
        else
        {
            slot = count++;
            if (slot == entries.Length)
                Array.Resize(ref entries, entries.Length * 2);
            entries[slot].Generation = 1;
        }
        ref var entry = ref entries[slot];
        entry.Array = Rent(length);
        entry.Length = length;
        return new BufferHandle<T>(slot, entry.Generation);
    }

    /// <summary>The buffer's elements, writable in place; the span is valid until the buffer is resized or freed.</summary>
    /// <exception cref="InvalidOperationException">The handle is null or freed.</exception>
    [Effect(Effects.None)]
    public Span<T> Get(BufferHandle<T> handle)
    {
        ref var entry = ref Live(handle);
        if (Unsafe.IsNullRef(ref entry))
            throw new InvalidOperationException("The buffer handle is null or freed.");
        return entry.Array.AsSpan(0, entry.Length);
    }

    /// <summary>The buffer's elements, valid until it is resized or freed; false when the handle is null or freed.</summary>
    [Effect(Effects.None)]
    public bool TryGet(BufferHandle<T> handle, out Span<T> span)
    {
        ref var entry = ref Live(handle);
        if (Unsafe.IsNullRef(ref entry))
        {
            span = default;
            return false;
        }
        span = entry.Array.AsSpan(0, entry.Length);
        return true;
    }

    /// <summary>Changes the length, keeping the leading elements. The handle stays valid.</summary>
    [Effect(Effects.Mutates)]
    public void Resize(BufferHandle<T> handle, int length)
    {
        world.CheckWritable();
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ref var entry = ref Live(handle);
        if (Unsafe.IsNullRef(ref entry))
            throw new InvalidOperationException("The buffer handle is null or freed.");
        if (length > entry.Array!.Length)
        {
            var grown = Rent(length);
            entry.Array.AsSpan(0, entry.Length).CopyTo(grown);
            Return(entry.Array);
            entry.Array = grown;
        }
        else if (length > entry.Length)
            entry.Array.AsSpan(entry.Length, length - entry.Length).Clear();
        entry.Length = length;
    }

    /// <summary>Frees the buffer; false when the handle is already stale.</summary>
    [Effect(Effects.Mutates)]
    public bool Free(BufferHandle<T> handle)
    {
        world.CheckWritable();
        ref var entry = ref Live(handle);
        if (Unsafe.IsNullRef(ref entry))
            return false;
        Return(entry.Array!);
        entry.Array = null;
        entry.Length = 0;
        entry.Generation = entry.Generation == uint.MaxValue ? 1 : entry.Generation + 1;
        free.Push(handle.Slot);
        return true;
    }

    void IBufferArena.Clear()
    {
        for (var slot = 1; slot < count; slot++)
        {
            ref var entry = ref entries[slot];
            if (entry.Array is { } array)
            {
                Return(array);
                entry.Array = null;
                entry.Length = 0;
                entry.Generation = entry.Generation == uint.MaxValue ? 1 : entry.Generation + 1;
                free.Push(slot);
            }
        }
    }

    sealed record State(Entry[] Entries, int Count, int[] Free);

    object IBufferArena.Save()
    {
        var saved = new Entry[count];
        for (var slot = 1; slot < count; slot++)
        {
            saved[slot] = entries[slot];
            if (entries[slot].Array is { } array)
                saved[slot].Array = array.AsSpan(0, entries[slot].Length).ToArray();
        }
        return new State(saved, count, free.ToArray());
    }

    void IBufferArena.Load(object? state)
    {
        for (var slot = 1; slot < count; slot++)
        {
            if (entries[slot].Array is { } array)
                Return(array);
        }
        Array.Clear(entries);
        free.Clear();
        if (state is not State saved)
        {
            count = 1;
            return;
        }
        if (entries.Length < saved.Count)
            entries = new Entry[saved.Entries.Length];
        for (var slot = 1; slot < saved.Count; slot++)
        {
            ref readonly var from = ref saved.Entries[slot];
            entries[slot].Generation = from.Generation;
            if (from.Array is null)
                continue;
            entries[slot].Array = Rent(from.Length);
            entries[slot].Length = from.Length;
            from.Array.CopyTo(entries[slot].Array.AsSpan());
        }
        count = saved.Count;
        for (var i = saved.Free.Length - 1; i >= 0; i--)
            free.Push(saved.Free[i]);
    }

    ref Entry Live(BufferHandle<T> handle)
    {
        if ((uint)handle.Slot - 1 >= (uint)(count - 1))
            return ref Unsafe.NullRef<Entry>();
        ref var entry = ref entries[handle.Slot];
        return ref entry.Array is not null && entry.Generation == handle.Generation
            ? ref entry
            : ref Unsafe.NullRef<Entry>();
    }

    T[] Rent(int length)
    {
        var capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(length, 4));
        var size = BitOperations.Log2((uint)capacity);
        if (pools[size] is { Count: > 0 } pool)
        {
            var array = pool.Pop();
            array.AsSpan(0, length).Clear();
            return array;
        }
        return new T[capacity];
    }

    void Return(T[] array) =>
        (pools[BitOperations.Log2((uint)array.Length)] ??= new Stack<T[]>()).Push(array);
}

internal interface IBufferArena
{
    void Clear();

    object Save();

    void Load(object? state);
}

internal static class BufferSlot<T>
    where T : unmanaged
{
    public static readonly int Index = AttachmentIds.Next();
}

public sealed partial class World
{
    object?[] arenas = [];

    /// <summary>This world's buffers of <typeparamref name="T"/>.</summary>
    [Effect(Effects.None)]
    public BufferArena<T> Buffers<T>()
        where T : unmanaged
    {
        var index = BufferSlot<T>.Index;
        if (index >= arenas.Length)
            Array.Resize(ref arenas, Math.Max(index + 1, arenas.Length * 2));
        return Unsafe.As<BufferArena<T>>(arenas[index] ??= new BufferArena<T>(this));
    }

    internal void FreeOwned<T>(BufferHandle<T> handle)
        where T : unmanaged
    {
        if (!handle.IsNull)
            Buffers<T>().Free(handle);
    }

    internal void FreeBuffers(int type, ref byte value)
    {
        var descriptor = registry[type];
        var offsets = descriptor.BufferHandleOffsets;
        var ops = descriptor.BufferOps;
        for (var i = 0; i < offsets.Length; i++)
            ops[i].Free(this, ref Unsafe.Add(ref value, offsets[i]));
    }

    internal void FreeReplacedBuffers(int type, ref byte before, ref byte after)
    {
        var descriptor = registry[type];
        var offsets = descriptor.BufferHandleOffsets;
        var ops = descriptor.BufferOps;
        for (var i = 0; i < offsets.Length; i++)
        {
            ref var outgoing = ref Unsafe.Add(ref before, offsets[i]);
            if (!Holds(descriptor, ref after, ops[i].HandleType, ref outgoing))
                ops[i].Free(this, ref outgoing);
        }
    }

    // A parallel body can neither allocate nor free (freeing would race the arena), so a handle may only move between fields there.
    internal void ReleaseBuffers<T>(int type, in T before, in T after, bool parallel)
        where T : struct
    {
        ref var outgoing = ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in before));
        ref var incoming = ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in after));
        if (!parallel)
        {
            FreeReplacedBuffers(type, ref outgoing, ref incoming);
            return;
        }
        var descriptor = registry[type];
        var offsets = descriptor.BufferHandleOffsets;
        var ops = descriptor.BufferOps;
        for (var i = 0; i < offsets.Length; i++)
        {
            if (
                !Holds(
                    descriptor,
                    ref incoming,
                    ops[i].HandleType,
                    ref Unsafe.Add(ref outgoing, offsets[i])
                )
                || !Holds(
                    descriptor,
                    ref outgoing,
                    ops[i].HandleType,
                    ref Unsafe.Add(ref incoming, offsets[i])
                )
            )
                throw new InvalidOperationException(
                    $"A parallel query dropped or took in a buffer handle of {descriptor.Name}, which would leak or alias a buffer; do it in a serial run."
                );
        }
    }

    static bool Holds(
        ComponentDescriptor descriptor,
        ref byte value,
        int handleType,
        ref byte handle
    )
    {
        var bits = Unsafe.ReadUnaligned<ulong>(ref handle);
        if (bits == 0)
            return true;
        var offsets = descriptor.BufferHandleOffsets;
        var ops = descriptor.BufferOps;
        for (var i = 0; i < offsets.Length; i++)
        {
            if (
                ops[i].HandleType == handleType
                && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, offsets[i])) == bits
            )
                return true;
        }
        return false;
    }

    void ClearBuffers()
    {
        foreach (var arena in arenas)
            (arena as IBufferArena)?.Clear();
    }
}
