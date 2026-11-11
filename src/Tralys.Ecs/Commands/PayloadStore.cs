using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

// Segmented so an outstanding ref into a payload survives growth.
internal abstract class PayloadStore
{
    protected const int SegmentBits = 6;
    protected const int SegmentSize = 1 << SegmentBits;

    public int Count;

    public abstract ref byte RowRef(int index);

    public abstract void CopyToColumn(int index, Array column, int row);

    public abstract bool WriteToColumn(int index, Array column, int row, bool unlessEqual);

    public abstract int Capture(ref byte value);

    public abstract void Reset();
}

internal sealed class PayloadStore<T> : PayloadStore
    where T : struct
{
    readonly List<T[]> segments = [];

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref segments[index >> SegmentBits][index & (SegmentSize - 1)];
    }

    public int Add(in T value)
    {
        var index = Count++;
        if ((index >> SegmentBits) == segments.Count)
            segments.Add(new T[SegmentSize]);
        this[index] = value;
        return index;
    }

    public override ref byte RowRef(int index) => ref Unsafe.As<T, byte>(ref this[index]);

    public override void CopyToColumn(int index, Array column, int row) =>
        Unsafe.As<T[]>(column)[row] = this[index];

    public override bool WriteToColumn(int index, Array column, int row, bool unlessEqual)
    {
        ref var cell = ref Unsafe.As<T[]>(column)[row];
        ref var incoming = ref this[index];
        if (
            unlessEqual
            && ComponentOpsOf<T>.Equals(
                ref Unsafe.As<T, byte>(ref cell),
                ref Unsafe.As<T, byte>(ref incoming)
            )
        )
            return false;
        cell = incoming;
        return true;
    }

    public override int Capture(ref byte value) => Add(in Unsafe.As<byte, T>(ref value));

    public override void Reset()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            for (var i = 0; i < Count; i++)
                this[i] = default;
        }
        Count = 0;
    }
}
