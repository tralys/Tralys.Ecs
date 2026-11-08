namespace Tralys.Ecs.Generated;

/// <summary>Implemented by <see cref="BufferHandle{T}"/> so generated metadata can handle an owned buffer without knowing its element type.</summary>
public interface IOwnedBuffer
{
    /// <summary>Frees the buffer <paramref name="handle"/> points at; a null handle is ignored.</summary>
    static abstract void Free(World world, ref byte handle);

    /// <summary>The buffer's elements as bytes, or null for a null or stale handle.</summary>
    static abstract byte[]? Copy(World world, ref byte handle);

    /// <summary>Allocates a buffer holding <paramref name="contents"/> and writes its handle over <paramref name="handle"/>.</summary>
    static abstract void Allocate(World world, ReadOnlySpan<byte> contents, ref byte handle);

    /// <summary>The size of one element in bytes; a buffer's contents are always a whole number of elements.</summary>
    static abstract int ElementSize { get; }
}
