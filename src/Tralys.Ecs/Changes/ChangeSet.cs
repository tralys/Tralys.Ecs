using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tralys.Ecs;

/// <summary>Entities and what each owes, as flag bits a consumer defines; <see cref="ChangeWatch{TBits}.Collect"/> fills it and the
/// consumer clears what it has done.</summary>
public sealed class ChangeSet<TBits>
    where TBits : unmanaged, Enum
{
    readonly Dictionary<Entity, ulong> marks = [];

    /// <summary>How many entities owe something.</summary>
    public int Count => marks.Count;

    /// <summary>Adds <paramref name="bits"/> to what <paramref name="entity"/> owes; no bits or a default handle does nothing.</summary>
    public void Mark(Entity entity, TBits bits)
    {
        var value = Bits(bits);
        if (value == 0 || entity.IsDefault)
            return;
        CollectionsMarshal.GetValueRefOrAddDefault(marks, entity, out _) |= value;
    }

    /// <summary>Whether <paramref name="entity"/> owes any of <paramref name="bits"/>.</summary>
    public bool IsMarked(Entity entity, TBits bits) =>
        marks.TryGetValue(entity, out var owed) && (owed & Bits(bits)) != 0;

    /// <summary>What <paramref name="entity"/> owes; no bits when it owes nothing.</summary>
    public TBits MarksOf(Entity entity) => From(marks.GetValueOrDefault(entity));

    /// <summary>Clears <paramref name="bits"/> from what <paramref name="entity"/> owes, and forgets it once it owes nothing.</summary>
    public void Unmark(Entity entity, TBits bits)
    {
        ref var owed = ref CollectionsMarshal.GetValueRefOrNullRef(marks, entity);
        if (Unsafe.IsNullRef(ref owed))
            return;
        owed &= ~Bits(bits);
        if (owed == 0)
            marks.Remove(entity);
    }

    /// <summary>Forgets <paramref name="entity"/>; false when it owed nothing.</summary>
    public bool Forget(Entity entity) => marks.Remove(entity);

    /// <summary>Forgets every entity.</summary>
    public void Clear() => marks.Clear();

    /// <summary>Each entity with what it owes; marking, unmarking or forgetting while enumerating throws.</summary>
    public Enumerator GetEnumerator() => new(marks.GetEnumerator());

    /// <summary>Enumerates a <see cref="ChangeSet{TBits}"/>.</summary>
    public struct Enumerator
    {
        Dictionary<Entity, ulong>.Enumerator inner;

        internal Enumerator(Dictionary<Entity, ulong>.Enumerator inner) => this.inner = inner;

        /// <summary>The entity and what it owes.</summary>
        public readonly (Entity Entity, TBits Owed) Current =>
            (inner.Current.Key, From(inner.Current.Value));

        /// <summary>Moves to the next entity.</summary>
        public bool MoveNext() => inner.MoveNext();
    }

    internal static ulong Bits(TBits bits) =>
        Unsafe.SizeOf<TBits>() switch
        {
            1 => Unsafe.As<TBits, byte>(ref bits),
            2 => Unsafe.As<TBits, ushort>(ref bits),
            4 => Unsafe.As<TBits, uint>(ref bits),
            _ => Unsafe.As<TBits, ulong>(ref bits),
        };

    internal static TBits From(ulong bits)
    {
        switch (Unsafe.SizeOf<TBits>())
        {
            case 1:
                var b = (byte)bits;
                return Unsafe.As<byte, TBits>(ref b);
            case 2:
                var s = (ushort)bits;
                return Unsafe.As<ushort, TBits>(ref s);
            case 4:
                var i = (uint)bits;
                return Unsafe.As<uint, TBits>(ref i);
            default:
                return Unsafe.As<ulong, TBits>(ref bits);
        }
    }
}
