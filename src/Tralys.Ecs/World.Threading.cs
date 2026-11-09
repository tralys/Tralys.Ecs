using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    int ownerThread;
    readonly bool checkThread;

    /// <summary>Hands the world to the calling thread; with <see cref="WorldOptions.CheckThread"/>, writes from any other thread
    /// throw.</summary>
    [Effect(Effects.None)]
    public void BindToCurrentThread() => ownerThread = Environment.CurrentManagedThreadId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CheckWritable()
    {
        if (parallelDepth != 0)
            ThrowParallel();
        if (checkThread)
            CheckOwner();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowParallel() =>
        throw new InvalidOperationException(
            "A partitioned query is running: change the world through the chunk's columns and Commands, not the world API."
        );

    [MethodImpl(MethodImplOptions.NoInlining)]
    void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException(
                "This world is owned by another thread; call BindToCurrentThread when handing it over."
            );
    }
}
