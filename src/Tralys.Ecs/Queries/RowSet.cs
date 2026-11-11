using System.Numerics;

namespace Tralys.Ecs;

/// <summary>The rows of a chunk a query visits, as indices into the chunk's spans, in ascending order.</summary>
public readonly struct RowSet(ulong mask)
{
    /// <summary>Rows the query visits.</summary>
    public int Count => BitOperations.PopCount(mask);

    /// <summary>Whether the query visits <paramref name="row"/>.</summary>
    public bool Contains(int row) => (uint)row < 64 && (mask & (1UL << row)) != 0;

    /// <summary>Walks the visited rows in ascending order.</summary>
    public Enumerator GetEnumerator() => new(mask);

    /// <summary>Walks a <see cref="RowSet"/>.</summary>
    public struct Enumerator(ulong bits)
    {
        ulong bits = bits;

        /// <summary>The row at the current position.</summary>
        public int Current { get; private set; }

        /// <summary>Moves to the next visited row; false past the last.</summary>
        public bool MoveNext()
        {
            if (bits == 0)
                return false;
            Current = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            return true;
        }
    }
}
