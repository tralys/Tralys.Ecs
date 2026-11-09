using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tralys.Ecs;

internal readonly struct TypeSlot(int column, int enable, int tracked)
{
    public readonly short Column = (short)column;
    public readonly short Enable = (short)enable;
    public readonly short Tracked = (short)tracked;
}

// Sized by the archetype, not the registry: thousands of worlds cannot afford a registry-wide table per archetype.
internal readonly struct TypeMap
{
    readonly ulong[] words;
    readonly ushort[] rank;
    readonly TypeSlot[] slots;

    public TypeMap(ulong[] words, TypeSlot[] slots)
    {
        if (slots.Length > short.MaxValue)
            throw new NotSupportedException(
                $"An archetype holds at most {short.MaxValue} component and tag types."
            );
        this.words = words;
        this.slots = slots;
        rank = new ushort[words.Length];
        for (int w = 0, total = 0; w < words.Length; w++)
        {
            rank[w] = (ushort)total;
            total += BitOperations.PopCount(words[w]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int IndexOf(int type)
    {
        var w = type >> 6;
        var bits = words;
        if ((uint)w >= (uint)bits.Length)
            return -1;
        var word = bits[w];
        var bit = 1UL << type;
        if ((word & bit) == 0)
            return -1;
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(rank), w)
            + BitOperations.PopCount(word & (bit - 1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TypeSlot SlotAt(int index) =>
        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(slots), index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ColumnOf(int type) => IndexOf(type) is var i and >= 0 ? SlotAt(i).Column : -1;
}
