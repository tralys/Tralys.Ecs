using System.Numerics;

namespace Tralys.Ecs;

internal readonly struct ArchetypeSignature : IEquatable<ArchetypeSignature>
{
    readonly ulong[] words;
    readonly int hash;

    public ArchetypeSignature(ulong[] words, bool detached)
    {
        this.words = words;
        Detached = detached;
        var h = new HashCode();
        foreach (var word in words)
            h.Add(word);
        h.Add(detached);
        hash = h.ToHashCode();
    }

    public static ArchetypeSignature Empty(int typeCount) =>
        new(new ulong[(typeCount + 63) >> 6], false);

    public bool Detached { get; }

    public ulong[] Words => words;

    public bool Has(int type)
    {
        var word = type >> 6;
        return (uint)word < (uint)words.Length && (words[word] & (1UL << type)) != 0;
    }

    public ArchetypeSignature With(int type)
    {
        var copy = (ulong[])words.Clone();
        copy[type >> 6] |= 1UL << type;
        return new(copy, Detached);
    }

    public ArchetypeSignature Without(int type)
    {
        var copy = (ulong[])words.Clone();
        copy[type >> 6] &= ~(1UL << type);
        return new(copy, Detached);
    }

    public ArchetypeSignature WithDetached(bool detached) => new((ulong[])words.Clone(), detached);

    public ArchetypeSignature WithAll(ReadOnlySpan<int> types, bool detached)
    {
        var copy = (ulong[])words.Clone();
        foreach (var type in types)
            copy[type >> 6] |= 1UL << type;
        return new(copy, detached);
    }

    public int[] Types()
    {
        var list = new List<int>();
        for (var w = 0; w < words.Length; w++)
        {
            var bits = words[w];
            while (bits != 0)
            {
                var bit = BitOperations.TrailingZeroCount(bits);
                list.Add((w << 6) + bit);
                bits &= bits - 1;
            }
        }
        return [.. list];
    }

    public bool Equals(ArchetypeSignature other) =>
        Detached == other.Detached && words.AsSpan().SequenceEqual(other.words);

    public override bool Equals(object? obj) => obj is ArchetypeSignature other && Equals(other);

    public override int GetHashCode() => hash;
}
