using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tralys.Ecs;

/// <summary>Entities and what each owes, as flag bits a consumer defines; <see cref="ChangeWatch{TBits}.Collect"/> fills it and the
/// consumer clears what it has done. Clearing the world or resetting it to a snapshot empties every set a watch has collected into, so
/// none holds a handle from before; several watches of one world may fill one set, and a watch of another world empties it first.</summary>
public sealed class ChangeSet<TBits>
    where TBits : unmanaged, Enum
{
    readonly Dictionary<Entity, ulong> marks = [];
    ChangeTracking? source;
    ulong epoch;

    /// <summary>How many entities owe something.</summary>
    public int Count => Stale ? 0 : marks.Count;

    // Reads never settle, so workers may read one set at once.
    [MemberNotNullWhen(true, nameof(source))]
    bool Stale => source is not null && source.Epoch != epoch;

    Dictionary<Entity, ulong> Settled
    {
        get
        {
            if (Stale)
            {
                marks.Clear();
                epoch = source.Epoch;
            }
            return marks;
        }
    }

    internal void Join(ChangeTracking tracking)
    {
        _ = Settled;
        if (source == tracking)
            return;
        if (source is not null)
            marks.Clear();
        source = tracking;
        epoch = tracking.Epoch;
    }

    /// <summary>Adds <paramref name="bits"/> to what <paramref name="entity"/> owes; no bits or a default handle does nothing.</summary>
    public void Mark(Entity entity, TBits bits)
    {
        var value = Bits(bits);
        if (value == 0 || entity.IsDefault)
            return;
        CollectionsMarshal.GetValueRefOrAddDefault(Settled, entity, out _) |= value;
    }

    /// <summary>Whether <paramref name="entity"/> owes any of <paramref name="bits"/>.</summary>
    public bool IsMarked(Entity entity, TBits bits) =>
        !Stale && marks.TryGetValue(entity, out var owed) && (owed & Bits(bits)) != 0;

    /// <summary>What <paramref name="entity"/> owes; no bits when it owes nothing.</summary>
    public TBits MarksOf(Entity entity) => From(Stale ? 0 : marks.GetValueOrDefault(entity));

    /// <summary>Clears <paramref name="bits"/> from what <paramref name="entity"/> owes, and forgets it once it owes nothing.</summary>
    public void Unmark(Entity entity, TBits bits)
    {
        ref var owed = ref CollectionsMarshal.GetValueRefOrNullRef(Settled, entity);
        if (Unsafe.IsNullRef(ref owed))
            return;
        owed &= ~Bits(bits);
        if (owed == 0)
            marks.Remove(entity);
    }

    /// <summary>Forgets <paramref name="entity"/>; false when it owed nothing.</summary>
    public bool Forget(Entity entity) => Settled.Remove(entity);

    /// <summary>Forgets every entity.</summary>
    public void Clear() => Settled.Clear();

    internal void ForgetGone(World world)
    {
        foreach (var entity in Settled.Keys)
        {
            if (!world.Exists(entity))
                marks.Remove(entity);
        }
    }

    /// <summary>Each entity with what it owes. Marking an entity the set does not hold while enumerating throws, as does clearing or
    /// resetting the world; unmarking, forgetting and marking one it holds do not.</summary>
    public Enumerator GetEnumerator() => new(this, Settled.GetEnumerator());

    /// <summary>Enumerates a <see cref="ChangeSet{TBits}"/>.</summary>
    public struct Enumerator
    {
        readonly ChangeTracking? source;
        readonly ulong epoch;
        Dictionary<Entity, ulong>.Enumerator inner;

        internal Enumerator(ChangeSet<TBits> set, Dictionary<Entity, ulong>.Enumerator inner)
        {
            source = set.source;
            epoch = set.epoch;
            this.inner = inner;
        }

        /// <summary>The entity and what it owes.</summary>
        public readonly (Entity Entity, TBits Owed) Current =>
            (inner.Current.Key, From(inner.Current.Value));

        /// <summary>Moves to the next entity.</summary>
        /// <exception cref="InvalidOperationException">The set gained an entity, or the world was cleared or reset, since enumeration
        /// began.</exception>
        public bool MoveNext()
        {
            if (source is not null && source.Epoch != epoch)
                throw new InvalidOperationException(
                    "The world was cleared or reset while this change set was being enumerated."
                );
            return inner.MoveNext();
        }
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
