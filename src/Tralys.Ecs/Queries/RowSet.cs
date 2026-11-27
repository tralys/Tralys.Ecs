using System.Numerics;
using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>The rows of a chunk a query visits, as indices into the chunk's spans, in ascending order. A row whose entity has since
/// recorded a change that takes it out of the query, such as a removal made by a serial body earlier in the walk, or whose ancestor has
/// recorded a destroy or detach, is not visited.</summary>
public readonly struct RowSet
{
    readonly ulong mask;
    readonly RowFilter filter;

    /// <summary>The rows set in <paramref name="mask"/>, one bit per row.</summary>
    public RowSet(ulong mask) => this.mask = mask;

    internal RowSet(ulong mask, RowFilter filter)
    {
        this.mask = mask;
        this.filter = filter;
    }

    /// <summary>Rows the query visits.</summary>
    public int Count
    {
        get
        {
            var count = BitOperations.PopCount(mask);
            for (var pending = filter.Pending(mask); pending != 0; pending &= pending - 1)
            {
                if (filter.Skips(BitOperations.TrailingZeroCount(pending)))
                    count--;
            }
            return count;
        }
    }

    /// <summary>Whether the query visits <paramref name="row"/>.</summary>
    public bool Contains(int row) =>
        (uint)row < 64 && (mask & (1UL << row)) != 0 && !filter.Skips(row);

    /// <summary>Walks the visited rows in ascending order.</summary>
    public Enumerator GetEnumerator() => new(mask, filter);

    /// <summary>Walks a <see cref="RowSet"/>.</summary>
    public struct Enumerator
    {
        ulong bits;
        readonly RowFilter filter;

        /// <summary>Walks the rows set in <paramref name="bits"/>.</summary>
        public Enumerator(ulong bits) => this.bits = bits;

        internal Enumerator(ulong bits, RowFilter filter)
        {
            this.bits = bits;
            this.filter = filter;
        }

        /// <summary>The row at the current position.</summary>
        public int Current { get; private set; }

        /// <summary>Moves to the next visited row; false past the last.</summary>
        public bool MoveNext()
        {
            while (bits != 0)
            {
                var row = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (filter.Skips(row))
                    continue;
                Current = row;
                return true;
            }
            return false;
        }
    }
}

internal readonly struct RowFilter(
    CachedQuery query,
    Archetype archetype,
    ArchetypeMatch match,
    int start
)
{
    readonly CachedQuery? query = query;
    readonly Archetype? archetype = archetype;
    readonly ArchetypeMatch? match = match;
    readonly int start = start;

    public ulong Pending(ulong mask) =>
        archetype is null || query is null ? 0
        : query.world.overlay.HasLifecycle ? mask
        : (archetype.PendingWords[start >> 6] >> (start & 63)) & mask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Skips(int row) =>
        archetype is not null
        && query is not null
        && match is not null
        && (archetype.IsPending(start + row) || query.world.overlay.HasLifecycle)
        && query.Skips(archetype, match, start + row);
}
