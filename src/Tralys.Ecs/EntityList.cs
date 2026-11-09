using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>Entities a store lists: an index key's holders, a parent's children, a relation target's sources or a row key's owners. It
/// reads the store's own storage and throws once what it lists changes, so copy it out with <see cref="CopyTo"/>, or take a
/// <c>Snapshot</c>, before an edit that changes what it lists; edits to another key, parent or target leave it valid. A list read
/// while its key had no entities may stay empty.</summary>
public readonly ref struct EntityList
{
    readonly ListArena<Entity>? lists;
    readonly int firstList;
    readonly int firstStamp;
    readonly int secondList;
    readonly int secondStamp;

    internal EntityList(ListArena<Entity> lists, int list)
        : this(lists, list, -1) { }

    internal EntityList(ListArena<Entity> lists, int firstList, int secondList)
    {
        this.lists = lists;
        this.firstList = firstList;
        this.secondList = secondList;
        firstStamp = firstList >= 0 ? lists.StampOf(firstList) : 0;
        secondStamp = secondList >= 0 ? lists.StampOf(secondList) : 0;
    }

    // Sliced on every read: growing another list moves the arena, and a span taken earlier would keep reading the abandoned array.
    ReadOnlySpan<Entity> First =>
        lists is not null && firstList >= 0 ? lists.Span(firstList) : default;

    ReadOnlySpan<Entity> Second =>
        lists is not null && secondList >= 0 ? lists.Span(secondList) : default;

    /// <summary>How many entities the list holds.</summary>
    public int Count
    {
        get
        {
            Check();
            return First.Length + Second.Length;
        }
    }

    /// <summary>The list holds no entities.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>The entity at <paramref name="index"/>, in the store's order.</summary>
    public Entity this[int index]
    {
        get
        {
            Check();
            var first = First;
            return index < first.Length ? first[index] : Second[index - first.Length];
        }
    }

    /// <summary>The list holds <paramref name="entity"/>; a linear scan.</summary>
    public bool Contains(Entity entity)
    {
        Check();
        return First.Contains(entity) || Second.Contains(entity);
    }

    /// <summary>Copies the entities into <paramref name="destination"/>, which must hold <see cref="Count"/>; the copy survives later
    /// edits.</summary>
    public void CopyTo(Span<Entity> destination)
    {
        Check();
        var first = First;
        first.CopyTo(destination);
        Second.CopyTo(destination[first.Length..]);
    }

    /// <summary>A new array of the entities, which survives later edits.</summary>
    public Entity[] ToArray()
    {
        var copy = new Entity[Count];
        CopyTo(copy);
        return copy;
    }

    /// <summary>Walks the entities in the store's order; it throws once what the list lists changes.</summary>
    public Enumerator GetEnumerator() => new(this);

    readonly void Check()
    {
        if (
            lists is not null
            && (
                (firstList >= 0 && lists.StampOf(firstList) != firstStamp)
                || (secondList >= 0 && lists.StampOf(secondList) != secondStamp)
            )
        )
            ThrowChanged();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowChanged() =>
        throw new InvalidOperationException(
            "The list changed after it was read; copy it out with CopyTo, or take a Snapshot, before an edit that changes what it lists."
        );

    /// <summary>Walks an <see cref="EntityList"/>.</summary>
    public ref struct Enumerator
    {
        readonly EntityList list;
        int at;

        internal Enumerator(EntityList list)
        {
            this.list = list;
            at = -1;
        }

        /// <summary>The entity at the current position.</summary>
        public readonly Entity Current => list[at];

        /// <summary>Advances; false past the last entity.</summary>
        public bool MoveNext() => ++at < list.Count;
    }
}
