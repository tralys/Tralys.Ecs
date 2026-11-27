namespace Tralys.Ecs.Generated;

/// <summary>Frees, copies and reallocates the buffer behind one buffer-handle field; emitted by the generator beside each buffer-handle
/// offset.</summary>
public readonly unsafe struct BufferOps
{
    [Effect(Effects.Mutates)]
    readonly delegate* <World, ref byte, void> free;

    [Effect(Effects.None)]
    readonly delegate* <World, ref byte, byte[]?> copy;

    [Effect(Effects.Mutates)]
    readonly delegate* <World, ReadOnlySpan<byte>, ref byte, void> allocate;
    internal readonly int ElementSize;

    // Equal for two fields whose handles index the same arena, so the same slot and generation name the same buffer.
    internal readonly int HandleType;
    internal readonly bool HoldsEntities;

    BufferOps(
        delegate* <World, ref byte, void> free,
        delegate* <World, ref byte, byte[]?> copy,
        delegate* <World, ReadOnlySpan<byte>, ref byte, void> allocate,
        int elementSize,
        int handleType,
        bool holdsEntities
    )
    {
        this.free = free;
        this.copy = copy;
        this.allocate = allocate;
        ElementSize = elementSize;
        HandleType = handleType;
        HoldsEntities = holdsEntities;
    }

    /// <summary>The operations of buffer-handle type <typeparamref name="THandle"/>; generated metadata emits one per buffer-handle field.</summary>
    public static BufferOps For<THandle>()
        where THandle : struct, IOwnedBuffer =>
        new(
            &Free<THandle>,
            &Copy<THandle>,
            &Allocate<THandle>,
            THandle.ElementSize,
            HandleTypeOf<THandle>.Id,
            typeof(THandle) == typeof(BufferHandle<Entity>)
        );

    static int handleTypes;

    static class HandleTypeOf<THandle>
    {
        public static readonly int Id = Interlocked.Increment(ref handleTypes);
    }

    static void Free<THandle>(World world, ref byte handle)
        where THandle : struct, IOwnedBuffer => THandle.Free(world, ref handle);

    static byte[]? Copy<THandle>(World world, ref byte handle)
        where THandle : struct, IOwnedBuffer => THandle.Copy(world, ref handle);

    static void Allocate<THandle>(World world, ReadOnlySpan<byte> contents, ref byte handle)
        where THandle : struct, IOwnedBuffer => THandle.Allocate(world, contents, ref handle);

    internal void Free(World world, ref byte handle) => free(world, ref handle);

    internal byte[]? Copy(World world, ref byte handle) => copy(world, ref handle);

    internal void Allocate(World world, ReadOnlySpan<byte> contents, ref byte handle) =>
        allocate(world, contents, ref handle);
}
