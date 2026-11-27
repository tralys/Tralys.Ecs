using System.Diagnostics;

namespace Tralys.Ecs;

/// <summary>Where a <see cref="ChangeSample"/>'s window ends, and in which life of the world it was taken: pass it to
/// <see cref="ChangeTracking.Commit"/> to consume the sample.</summary>
public readonly struct ChangeWindow
{
    internal ChangeWindow(ulong epoch, ulong until)
    {
        Epoch = epoch;
        Until = until;
    }

    internal ulong Epoch { get; }
    internal ulong Until { get; }
}

/// <summary>One cursor sample: the values and rows stamped in the version window (<see cref="Since"/>, <see cref="Until"/>]. Adds of
/// untracked types, removals and disables leave no stamp; a <see cref="ChangeWatch{TBits}"/> hears those.</summary>
public readonly ref struct ChangeSample
{
    readonly World world;
    readonly CursorOptions options;
    readonly ulong epoch;

    internal ChangeSample(World world, ulong since, ulong until, CursorOptions options, ulong epoch)
    {
        this.world = world;
        Since = since;
        Until = until;
        this.options = options;
        this.epoch = epoch;
    }

    /// <summary>The version the cursor last committed; the window excludes it. Zero after the world was reset to a snapshot, so the
    /// sample holds every stamped value.</summary>
    public ulong Since { get; }

    /// <summary>The world version when the sample was taken; the window includes it.</summary>
    public ulong Until { get; }

    /// <summary>The window to pass to <see cref="ChangeTracking.Commit"/> to consume the sample.</summary>
    public ChangeWindow Window => new(epoch, Until);

    /// <summary>The entities holding <typeparamref name="T"/> present whose value was stamped in the window, skipping an entity pending
    /// destroy or detached unless the cursor's options include it; removals and disables are not seen, and a value stamped again after
    /// <see cref="Until"/> shows in the next sample instead.</summary>
    public ChangedValues<T> Changed<T>()
        where T : struct, IComponent, ITracked =>
        new(world, world.registry.IndexOf<T>(), Since, Until, options);

    /// <summary>The rows of <typeparamref name="TRow"/> stamped in the window, each with its owner and key, as <see cref="Changed{T}"/>
    /// is for components: a row added in the window is included, an owner pending destroy or detached is skipped unless the cursor's
    /// options include it, removals are not seen, a write inside <see cref="World.Untracked"/> stamps none, and a row stamped again after <see cref="Until"/> shows in the next sample instead.
    /// Only owners with a row stamped after <see cref="Since"/> are visited, and one walk of <typeparamref name="TRow"/> may be open at a
    /// time: starting another before it ends throws.</summary>
    public ChangedRows<TKey, TRow> ChangedRows<TKey, TRow>()
        where TKey : notnull
        where TRow : struct, IRow<TKey>, ITracked => new(world, Since, Until, options);
}

/// <summary>One entity whose value of <typeparamref name="T"/> was stamped inside a sample's window.</summary>
public readonly ref struct ChangedValue<T>
    where T : struct
{
    readonly ref readonly T value;

    internal ChangedValue(Entity entity, ref readonly T value, bool added, ulong version)
    {
        Entity = entity;
        this.value = ref value;
        Added = added;
        Version = version;
    }

    /// <summary>The entity holding the value.</summary>
    public Entity Entity { get; }

    /// <summary>The entity's current value, by reference into storage; valid until the next structural change.</summary>
    public ref readonly T Value => ref value;

    /// <summary>The component itself arrived inside the window, rather than only its value changing.</summary>
    public bool Added { get; }

    /// <summary>The version the value was last stamped at.</summary>
    public ulong Version { get; }
}

/// <summary>Values of <typeparamref name="T"/> stamped inside a version window; chunks whose newest stamp is older are skipped whole.</summary>
public readonly ref struct ChangedValues<T>
    where T : struct
{
    readonly World world;
    readonly int type;
    readonly ulong since;
    readonly ulong until;
    readonly CursorOptions options;

    internal ChangedValues(World world, int type, ulong since, ulong until, CursorOptions options)
    {
        Debug.Assert(world.registry[type].IsTracked);
        this.world = world;
        this.type = type;
        this.since = since;
        this.until = until;
        this.options = options;
    }

    /// <summary>Walks the stamped values in storage order.</summary>
    public Enumerator GetEnumerator() => new(world, type, since, until, options);

    /// <summary>Walks a <see cref="ChangedValues{T}"/>.</summary>
    public ref struct Enumerator
    {
        readonly World world;
        readonly int type;
        readonly ulong since;
        readonly ulong until;
        readonly CursorOptions options;
        readonly SparseStorage<T>? sparse;
        int archetype;
        int chunk;
        int row;
        int end;
        int tracked;
        int column;
        int enable;
        Archetype? current;
        Entity entity;
        ulong stamp;
        bool added;
        ref T value;

        internal Enumerator(World world, int type, ulong since, ulong until, CursorOptions options)
        {
            this.world = world;
            this.type = type;
            this.since = since;
            this.until = until;
            this.options = options;
            var sparseSlot = world.layout.SparseSlot[type];
            sparse = sparseSlot >= 0 ? (SparseStorage<T>?)world.sparse[sparseSlot] : null;
            archetype = -1;
            tracked = -1;
            column = -1;
            enable = -1;
            chunk = -1;
            row = -1;
        }

        /// <summary>The value at the current position.</summary>
        public readonly ChangedValue<T> Current => new(entity, ref value, added, stamp);

        /// <summary>Moves to the next value stamped inside the window; false past the last.</summary>
        public bool MoveNext()
        {
            if (sparse is not null)
            {
                while (++row < sparse.Count)
                {
                    stamp = sparse.Changed[row];
                    if (
                        stamp > since
                        && stamp <= until
                        && world.Admits(sparse.Owners[row], options)
                    )
                    {
                        entity = sparse.Owners[row];
                        added = sparse.Added[row] > since;
                        value = ref sparse.Values[row];
                        return true;
                    }
                }
                return false;
            }
            while (true)
            {
                if (current is not null)
                {
                    while (++row < end && row < current.Count)
                    {
                        stamp = current.ChangedAt(tracked, row);
                        if (
                            stamp <= since
                            || stamp > until
                            || (enable >= 0 && !current.IsEnabled(enable, row))
                            || (
                                !options.IncludePendingDestroy
                                && (current.IsPendingDestroy(row) || current.HasAncestor(0, row))
                            )
                        )
                            continue;
                        entity = current.Entities[row];
                        added = current.AddedAt(tracked, row) > since;
                        value = ref column >= 0
                            ? ref current.Values<T>(column)[row]
                            : ref TagValue<T>.Value;
                        return true;
                    }
                }
                if (!NextChunk())
                    return false;
            }
        }

        bool NextChunk()
        {
            var archetypes = world.archetypes;
            while (true)
            {
                if (tracked >= 0 && archetype >= 0)
                {
                    var a = archetypes[archetype];
                    while (++chunk < a.ChunkCount)
                    {
                        if (a.MaxChangedIn(chunk, tracked) <= since)
                            continue;
                        current = a;
                        row = (chunk * a.ChunkCapacity) - 1;
                        end = row + 1 + a.ChunkCapacity;
                        return true;
                    }
                }
                if (++archetype >= archetypes.Count)
                    return false;
                var next = archetypes[archetype];
                tracked =
                    next.Signature.Has(type) && (options.IncludeDetached || !next.Detached)
                        ? next.TrackedSlot(type)
                        : -1;
                column = tracked >= 0 ? next.ColumnSlot(type) : -1;
                enable = tracked >= 0 ? next.EnableSlot(type) : -1;
                chunk = -1;
                current = null;
            }
        }
    }
}

internal static class TagValue<T>
    where T : struct
{
    public static T Value;
}
