using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    /// <summary>This world's pooled scratch arrays, for the owner thread.</summary>
    public WorldScratch Scratch => scratch ??= new WorldScratch(this);

    WorldScratch? scratch;
}

/// <summary>Per-world pooled scratch arrays, replacing thread-static scratch. Owner thread only: a partitioned query body cannot rent.</summary>
public sealed class WorldScratch
{
    readonly World world;
    object?[] pools = [];

    internal WorldScratch(World world) => this.world = world;

    /// <summary>A pooled buffer whose <see cref="ScratchBuffer{T}.Span"/> is exactly <paramref name="minimumLength"/> long; its contents
    /// are not cleared, and disposing it returns the array.</summary>
    /// <exception cref="InvalidOperationException">A partitioned query body is running.</exception>
    [Effect(Effects.OwnerThread)]
    public ScratchBuffer<T> Rent<T>(int minimumLength)
    {
        if (world.parallelDepth != 0)
            throw new InvalidOperationException(
                "World scratch belongs to the owner thread, so a partitioned query body cannot take a snapshot; take it before or after the run."
            );
        var pool = Pool<T>();
        var fits = pool.TryPop(out var array) && array.Length >= minimumLength;
        if (!fits)
            array = new T[Math.Max(16, (int)BitOperationsCeil((uint)minimumLength))];
        return new ScratchBuffer<T>(pool, array!, minimumLength);
    }

    Stack<T[]> Pool<T>()
    {
        var index = ScratchSlot<T>.Index;
        if (index >= pools.Length)
            Array.Resize(ref pools, Math.Max(index + 1, pools.Length * 2));
        return Unsafe.As<Stack<T[]>>(pools[index] ??= new Stack<T[]>());
    }

    static uint BitOperationsCeil(uint value) =>
        System.Numerics.BitOperations.RoundUpToPowerOf2(Math.Max(value, 1));
}

/// <summary>A rented array from <see cref="WorldScratch"/>; dispose it to return the array to the pool.</summary>
[MustDispose("its array never returns to the pool")]
public ref struct ScratchBuffer<T>
{
    Stack<T[]>? pool;
    readonly T[] array;

    internal ScratchBuffer(Stack<T[]> pool, T[] array, int length)
    {
        this.pool = pool;
        this.array = array;
        Span = array.AsSpan(0, length);
    }

    /// <summary>The rented elements, exactly the requested length; do not use it once the buffer is disposed.</summary>
    public Span<T> Span { get; }

    /// <summary>Returns the array to the pool, clearing it first if <typeparamref name="T"/> holds references; a second dispose does
    /// nothing.</summary>
    public void Dispose()
    {
        if (pool is null)
            return;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            Span.Clear();
        pool.Push(array);
        pool = null;
    }
}

internal static class ScratchSlot<T>
{
    public static readonly int Index = ScratchIds.Next();
}

internal static class ScratchIds
{
    static int next;

    public static int Next() => Interlocked.Increment(ref next) - 1;
}
