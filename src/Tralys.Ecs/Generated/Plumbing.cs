namespace Tralys.Ecs.Generated;

/// <summary>The runtime entry points generated code calls. Hand-written code uses the generated typed accessors and the <see cref="World"/>
/// API instead.</summary>
public static class Plumbing
{
    /// <summary>Opens a run for <paramref name="system"/> unless one is already open, as when a scheduler or another system's run called in.</summary>
    public static SystemRunScope EnsureSystemRun(World world, SystemId system) =>
        world.EnsureSystemRun(system);

    /// <summary>The descriptor of <typeparamref name="T"/> built from its generated metadata, once per type in a generated module.</summary>
    public static ComponentDescriptor CreateDescriptor<T>(ComponentInfo info)
        where T : struct => ComponentDescriptor.Create<T>(info);

    /// <summary>The world <paramref name="lookup"/> is bound to, behind the generated typed index accessors.</summary>
    public static World WorldOf<T>(IndexLookup<T> lookup)
        where T : struct, IComponent => lookup.World;

    /// <summary>The unique index at position <paramref name="ordinal"/> among <typeparamref name="TComponent"/>'s declared indexes, behind its generated accessor.</summary>
    public static UniqueIndex<TKey> UniqueIndexOf<TComponent, TKey>(World world, int ordinal)
        where TComponent : struct, IComponent
        where TKey : notnull => world.UniqueIndexOf<TComponent, TKey>(ordinal);

    /// <summary>The multi index at position <paramref name="ordinal"/> among <typeparamref name="TComponent"/>'s declared indexes, behind its generated accessor.</summary>
    public static MultiIndex<TKey> MultiIndexOf<TComponent, TKey>(World world, int ordinal)
        where TComponent : struct, IComponent
        where TKey : notnull => world.MultiIndexOf<TComponent, TKey>(ordinal);

    /// <summary>The ordered index at position <paramref name="ordinal"/> among <typeparamref name="TComponent"/>'s declared indexes, behind its generated accessor.</summary>
    public static OrderedIndex<TKey> OrderedIndexOf<TComponent, TKey>(World world, int ordinal)
        where TComponent : struct, IComponent
        where TKey : notnull => world.OrderedIndexOf<TComponent, TKey>(ordinal);

    /// <summary>The value of <typeparamref name="T"/> on <paramref name="entity"/>'s parent, for generated <see cref="Parent{T}"/> terms.</summary>
    public static Parent<T> ParentTerm<T>(World world, Entity entity)
        where T : struct, IComponent => world.ParentTerm<T>(entity);

    /// <summary>The keyed rows of <typeparamref name="TRow"/> on <paramref name="entity"/>, behind the generated per-row-type accessor
    /// such as <c>world.EffectRows(entity)</c>. Reading never creates storage.</summary>
    public static Rows<TKey, TRow> Rows<TKey, TRow>(World world, Entity entity)
        where TKey : notnull
        where TRow : struct, IRow<TKey> => world.Rows<TKey, TRow>(entity);

    /// <summary>As <see cref="Rows{TKey, TRow}(World, Entity)"/>, behind <c>entity.EffectRows()</c>; for <c>default(WorldEntity)</c>, rows
    /// bound to no world.</summary>
    public static Rows<TKey, TRow> Rows<TKey, TRow>(WorldEntity entity)
        where TKey : notnull
        where TRow : struct, IRow<TKey> =>
        entity.World is null ? default : entity.World.Rows<TKey, TRow>(entity.Entity);

    /// <summary>The recorded form of <see cref="Rows{TKey, TRow}(World, Entity)"/>, behind <c>commands.EffectRows(entity)</c>.</summary>
    public static CommandRows<TKey, TRow> Rows<TKey, TRow>(Commands commands, Entity entity)
        where TKey : notnull
        where TRow : struct, IRow<TKey> => commands.Rows<TKey, TRow>(entity);

    /// <summary>The rows the query visits in <paramref name="chunk"/>, one bit per row.</summary>
    public static ulong RowMask(in ChunkView chunk) => chunk.RowMask;

    /// <summary>The column of value term <paramref name="term"/>, or an empty span when the archetype lacks it.</summary>
    public static Span<T> Column<T>(in ChunkView chunk, int term) => chunk.Column<T>(term);

    /// <summary>The column of value term <paramref name="term"/> for writing; pass each written row that is no longer <see cref="Same{T}"/> to <see cref="Changed{T}"/>.</summary>
    public static TrackedSpan<T> Tracked<T>(in ChunkView chunk, int term)
        where T : struct, IMutableComponent => chunk.Tracked<T>(term);

    /// <summary>Value term <paramref name="term"/> of a type with no index, no propagated changes and no buffers, for
    /// <see cref="StampIfChanged{T}"/>.</summary>
    /// <exception cref="InvalidOperationException">The type is indexed, propagates changes or owns buffers.</exception>
    public static TrackedSpan<T> StampOnly<T>(in ChunkView chunk, int term)
        where T : struct, IMutableComponent => chunk.StampOnly<T>(term);

    /// <summary>Whether value term <paramref name="term"/> is present and enabled on <paramref name="row"/>.</summary>
    public static bool HasRow(in ChunkView chunk, int term, int row) => chunk.HasRow(term, row);

    /// <summary>The sparse storage of value term <paramref name="term"/>.</summary>
    public static SparseTerm<T> Sparse<T>(in ChunkView chunk, int term)
        where T : struct, IComponent => chunk.Sparse<T>(term);

    /// <summary>Adds <paramref name="row"/>'s entity to the snapshot of a collect-then-act run.</summary>
    public static void Collect(in ChunkView chunk, int row) => chunk.Collect(row);

    /// <summary>Whether <see cref="Changed{T}"/> has work to do: the row is stamped, or the type is indexed or owns buffers.</summary>
    public static bool Compares<T>(in TrackedSpan<T> span)
        where T : struct, IMutableComponent => span.Compares;

    /// <summary>A writable ref to <paramref name="row"/>'s value that stamps nothing until the row is released.</summary>
    public static ref T Unstamped<T>(in TrackedSpan<T> span, int row)
        where T : struct, IMutableComponent => ref span.Unstamped(row);

    /// <summary>Stamps <paramref name="row"/> if it differs from <paramref name="before"/>, and nothing else.</summary>
    public static void StampIfChanged<T>(in TrackedSpan<T> span, int row, in T before)
        where T : struct, IMutableComponent => span.StampIfChanged(row, in before);

    /// <summary>True when <paramref name="current"/> holds the same bytes as <paramref name="before"/>; a type holding references never
    /// does.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining
    )]
    public static bool Same<T>(in T before, in T current)
        where T : struct =>
        ComponentOpsOf<T>.Equals(
            ref System.Runtime.CompilerServices.Unsafe.As<T, byte>(
                ref System.Runtime.CompilerServices.Unsafe.AsRef(in before)
            ),
            ref System.Runtime.CompilerServices.Unsafe.As<T, byte>(
                ref System.Runtime.CompilerServices.Unsafe.AsRef(in current)
            )
        );

    /// <summary>For a <paramref name="row"/> that no longer matches <paramref name="before"/>: stamps it, moves its index entries and
    /// frees replaced buffers.</summary>
    public static void Changed<T>(in TrackedSpan<T> span, int row, in T before)
        where T : struct, IMutableComponent => span.Changed(row, in before);

    /// <summary>The entity's value, or a null ref with <paramref name="slot"/> -1 when it has none.</summary>
    public static ref T Find<T>(in SparseTerm<T> term, Entity entity, out int slot)
        where T : struct, IComponent => ref term.FindSlot(entity, out slot);

    /// <summary>Whether <see cref="Release{T}(in SparseTerm{T}, int, in T)"/> has work to do: writes are stamped, or the type is indexed or owns buffers.</summary>
    public static bool Compares<T>(in SparseTerm<T> term)
        where T : struct, IComponent => term.Compares;

    /// <summary>If the value in <paramref name="slot"/> differs from <paramref name="before"/>, stamps it, moves its index entries and frees replaced buffers.</summary>
    public static void Release<T>(in SparseTerm<T> term, int slot, in T before)
        where T : struct, IComponent => term.Release(slot, in before);

    /// <summary>An optional term over <paramref name="value"/>; a null ref makes it empty.</summary>
    public static Optional<T> Optional<T>(ref readonly T value)
        where T : struct, IDeclared => new(in value);

    /// <summary>A writable optional term over <paramref name="value"/>; a null ref makes it empty.</summary>
    public static OptionalRef<T> OptionalRef<T>(ref T value)
        where T : struct, IMutableComponent => new(ref value);
}
