using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>An entity's keyed rows of <typeparamref name="TRow"/> as a <see cref="WorldReader"/> reads them, in insertion order. Rows
/// added or removed during the walk it was taken in appear once the walk closes.</summary>
public readonly ref struct RowsReader<TKey, TRow>
    where TKey : notnull
    where TRow : struct, IRow<TKey>
{
    readonly World? world;
    readonly Entity entity;

    internal RowsReader(World world, Entity entity)
    {
        this.world = world;
        this.entity = entity;
    }

    Rows<TKey, TRow> Rows => world is null ? default : new(world, entity);

    /// <summary>How many rows the entity has.</summary>
    public int Count => Rows.Count;

    /// <inheritdoc cref="Rows{TKey, TRow}.Contains"/>
    [Effect(Effects.None)]
    public bool Contains(in TKey key) => Rows.Contains(key);

    /// <inheritdoc cref="Rows{TKey, TRow}.TryGet"/>
    [Effect(Effects.None)]
    public bool TryGet(in TKey key, out TRow row) => Rows.TryGet(key, out row);

    /// <inheritdoc cref="Rows{TKey, TRow}.Find"/>
    [Effect(Effects.None)]
    public Optional<TRow> Find(in TKey key) => Rows.Find(key);

    /// <summary>Walks the rows in insertion order.</summary>
    [Effect(Effects.None)]
    public Enumerator GetEnumerator() => new(world?.FindRows<TKey, TRow>()?.BlockOf(entity));

    /// <summary>Walks one entity's rows; outside a walk, a row added or removed while it walks makes it throw rather than misread.</summary>
    public ref struct Enumerator
    {
        readonly RowStore<TKey, TRow>.Block? block;
        readonly int end;
        readonly int moves;
        int index;

        internal Enumerator(RowStore<TKey, TRow>.Block? block)
        {
            this.block = block;
            end = block?.Count ?? 0;
            moves = block?.Moves ?? 0;
            index = -1;
        }

        /// <summary>The current row by <c>ref readonly</c>.</summary>
        /// <exception cref="InvalidOperationException">The entity's rows moved since the enumeration began.</exception>
        public readonly ref readonly TRow Current
        {
            get
            {
                if (block is null)
                    return ref Unsafe.NullRef<TRow>();
                CheckMoves();
                return ref block.Rows[index];
            }
        }

        /// <summary>Advances to the next row; false when none is left.</summary>
        /// <exception cref="InvalidOperationException">The entity's rows moved since the enumeration began.</exception>
        [Effect(Effects.None)]
        public bool MoveNext()
        {
            if (block is null)
                return false;
            CheckMoves();
            while (++index < end)
            {
                if (!block.Dead[index])
                    return true;
            }
            return false;
        }

        readonly void CheckMoves()
        {
            if (block is not null && block.Moves != moves)
                throw new InvalidOperationException(
                    "The entity's rows were added to or removed from while a reader enumerated them; enumerate through Rows to change them as you go."
                );
        }
    }
}
