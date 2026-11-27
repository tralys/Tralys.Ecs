using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>Receives an entity's components from <see cref="WorldIntrospection.VisitEntity{TVisitor}"/>, each by its own type, so no value
/// is boxed.</summary>
public interface IComponentVisitor
{
    /// <summary>Receives one component of the entity with its descriptor; a tag arrives as a default <typeparamref name="T"/>.</summary>
    void Visit<T>(ComponentDescriptor descriptor, in T value)
        where T : struct;
}

/// <summary>One archetype: its component types, entities and chunk use.</summary>
public readonly struct ArchetypeInfo
{
    readonly Archetype archetype;

    internal ArchetypeInfo(Archetype archetype) => this.archetype = archetype;

    /// <summary>The archetype's position in <see cref="WorldIntrospection.Archetypes"/>.</summary>
    public int Id => archetype.Id;

    /// <summary>Dense indices of the archetype's column and tag types.</summary>
    public ReadOnlySpan<int> Types => archetype.Types;

    /// <summary>The archetype's entities in row order; valid until the next structural change.</summary>
    public ReadOnlySpan<Entity> Entities => archetype.Entities.AsSpan(0, archetype.Count);

    /// <summary>Entities stored in the archetype, pending destroys included.</summary>
    public int EntityCount => archetype.Count;

    /// <summary>True for the detached partition, which queries skip unless they include detached entities.</summary>
    public bool Detached => archetype.Detached;

    /// <summary>Rows per chunk, a power of two.</summary>
    public int ChunkCapacity => archetype.ChunkCapacity;

    /// <summary>Chunks holding at least one entity.</summary>
    public int ChunkCount => archetype.ChunkCount;

    /// <summary>Entities over the rows of the chunks in use; 0 for an empty archetype.</summary>
    public double Fill =>
        archetype.Count == 0
            ? 0
            : (double)archetype.Count / (archetype.ChunkCount * archetype.ChunkCapacity);

    /// <summary>Bytes one full chunk holds in entity handles, component columns and change stamps.</summary>
    public int BytesPerChunk
    {
        get
        {
            var row = Unsafe.SizeOf<Entity>() + (archetype.TrackedTypes.Length * 16);
            foreach (var size in archetype.ColumnSizes)
                row += size;
            return row * archetype.ChunkCapacity;
        }
    }

    /// <summary>Rows allocated, in use or not; an archetype keeps them until <see cref="World.TrimExcess"/> finds it empty.</summary>
    public int AllocatedRows => archetype.Capacity;
}

/// <summary>A cached query and what it currently matches.</summary>
public readonly struct QueryInfo
{
    readonly CachedQuery query;

    internal QueryInfo(CachedQuery query) => this.query = query;

    /// <summary>The description the query was created from.</summary>
    public QueryDesc Desc => query.Desc;

    /// <summary>Archetypes the query matches now.</summary>
    public int MatchedArchetypes => query.MatchedArchetypeCount;

    /// <summary>Entities in the matched archetypes, before row filters: pending destroys, disabled terms and sparse terms are not
    /// applied.</summary>
    public int MatchedEntities => query.MatchedRowCount;
}

/// <summary>Read-only views of a world's layout and bookkeeping. Every read is allocation-free once warm, so the tick path may sample it.</summary>
public readonly struct WorldIntrospection
{
    readonly World world;

    internal WorldIntrospection(World world) => this.world = world;

    /// <summary>What the most recent playback did, including one that threw after completing.</summary>
    public PlaybackStats LastPlayback => world.lastPlayback;

    /// <summary>Archetypes the world has made, empty ones included.</summary>
    public int ArchetypeCount => world.archetypes.Count;

    /// <summary>Chunks holding at least one entity, over every archetype.</summary>
    public int ChunkCount
    {
        get
        {
            var chunks = 0;
            foreach (var archetype in world.archetypes)
                chunks += archetype.ChunkCount;
            return chunks;
        }
    }

    /// <summary>Entities over the rows of every chunk in use; 0 for an empty world.</summary>
    public double MeanChunkFill
    {
        get
        {
            long rows = 0,
                entities = 0;
            foreach (var archetype in world.archetypes)
            {
                rows += (long)archetype.ChunkCount * archetype.ChunkCapacity;
                entities += archetype.Count;
            }
            return rows == 0 ? 0 : (double)entities / rows;
        }
    }

    /// <summary>Every archetype, empty ones included, in creation order.</summary>
    public ArchetypeEnumerable Archetypes => new(world.archetypes);

    /// <summary>The descriptor of every type the registry holds, in ordinal name order.</summary>
    public ReadOnlySpan<ComponentDescriptor> Components => world.registry.Descriptors;

    /// <summary>Every cached query the world has created.</summary>
    public QueryEnumerable Queries => new(world.liveQueries);

    /// <summary>Rises whenever the world changes in a way a <see cref="LogKind"/> names, except an overwrite of a
    /// <see cref="ReplaceOnlyAttribute"/> component, and stays put otherwise; how far one change moves it is unspecified.</summary>
    public long StructuralChanges => world.changes.structuralChanges;

    /// <summary>Chunks a query run handed to its body, since the world was created.</summary>
    public long ChunksVisited => world.chunksVisited;

    /// <summary>Chunks a query run looked at and skipped because no row passed its filters, since the world was created.</summary>
    public long ChunksSkipped => world.chunksSkipped;

    /// <summary>Passes each stored component and tag of <paramref name="entity"/>, enabled or disabled (<see cref="World.Has{T}"/> tells
    /// which), to <paramref name="visitor"/>: archetype columns and tags in signature order, then sparse components and sparse tags. Adds waiting for playback are not visited. The visit is a
    /// serial walk, so a structural change the visitor makes records and applies when the outermost walk closes. Under NativeAOT the first
    /// visit with a new visitor type allocates the runtime's dispatch entry; later ones allocate nothing.</summary>
    public void VisitEntity<TVisitor>(Entity entity, ref TVisitor visitor)
        where TVisitor : struct, IComponentVisitor
    {
        ref var slot = ref world.Slot(entity);
        var iteration = world.BeginIteration();
        try
        {
            var registry = world.registry;
            if (slot.Archetype is { } archetype)
            {
                foreach (var type in archetype.Types)
                {
                    var descriptor = registry[type];
                    var column = archetype.ColumnSlot(type);
                    if (column >= 0)
                        descriptor.Column.Visit(
                            descriptor,
                            ref archetype.RowRef(column, slot.Row),
                            ref visitor
                        );
                    else
                        descriptor.Column.VisitDefault(descriptor, ref visitor);
                }
            }
            if (slot.SparseCount == 0)
                return;
            foreach (var storage in world.sparse)
            {
                if (storage is null || storage.SlotOf(entity.Index) is not (var at and >= 0))
                    continue;
                var descriptor = registry[storage.Type];
                descriptor.Column.Visit(descriptor, ref storage.RowRef(at), ref visitor);
            }
            foreach (var tags in world.sparseTags)
            {
                if (tags is null || !tags.Contains(entity.Index))
                    continue;
                var descriptor = registry[tags.Type];
                descriptor.Column.VisitDefault(descriptor, ref visitor);
            }
        }
        catch (Exception error)
        {
            iteration.Fault(error);
            throw;
        }
        finally
        {
            iteration.Dispose();
        }
    }

    /// <summary>The world's archetypes, by <see cref="ArchetypeInfo.Id"/>.</summary>
    public readonly struct ArchetypeEnumerable
    {
        readonly List<Archetype> archetypes;

        internal ArchetypeEnumerable(List<Archetype> archetypes) => this.archetypes = archetypes;

        /// <summary>Archetypes in the world.</summary>
        public int Count => archetypes.Count;

        /// <summary>The archetype whose <see cref="ArchetypeInfo.Id"/> is <paramref name="index"/>.</summary>
        public ArchetypeInfo this[int index] => new(archetypes[index]);

        /// <summary>Walks the archetypes in creation order.</summary>
        public Enumerator GetEnumerator() => new(archetypes);

        /// <summary>Walks an <see cref="ArchetypeEnumerable"/>.</summary>
        public struct Enumerator
        {
            readonly List<Archetype> archetypes;
            int index;

            internal Enumerator(List<Archetype> archetypes)
            {
                this.archetypes = archetypes;
                index = -1;
            }

            /// <summary>The archetype at the current position.</summary>
            public readonly ArchetypeInfo Current => new(archetypes[index]);

            /// <summary>Moves to the next archetype; false past the last.</summary>
            public bool MoveNext() => ++index < archetypes.Count;
        }
    }

    /// <summary>The world's cached queries, in creation order.</summary>
    public readonly struct QueryEnumerable
    {
        readonly List<CachedQuery> queries;

        internal QueryEnumerable(List<CachedQuery> queries) => this.queries = queries;

        /// <summary>Cached queries in the world.</summary>
        public int Count => queries.Count;

        /// <summary>The query at <paramref name="index"/>, in creation order.</summary>
        public QueryInfo this[int index] => new(queries[index]);

        /// <summary>Walks the queries in creation order.</summary>
        public Enumerator GetEnumerator() => new(queries);

        /// <summary>Walks a <see cref="QueryEnumerable"/>.</summary>
        public struct Enumerator
        {
            readonly List<CachedQuery> queries;
            int index;

            internal Enumerator(List<CachedQuery> queries)
            {
                this.queries = queries;
                index = -1;
            }

            /// <summary>The query at the current position.</summary>
            public readonly QueryInfo Current => new(queries[index]);

            /// <summary>Moves to the next query; false past the last.</summary>
            public bool MoveNext() => ++index < queries.Count;
        }
    }
}

public sealed partial class World
{
    internal long chunksVisited;
    internal long chunksSkipped;

    /// <summary>Read-only, allocation-free views of this world's layout and bookkeeping.</summary>
    public WorldIntrospection Introspection => new(this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool CountChunk(ulong rows)
    {
        if (rows == 0)
        {
            chunksSkipped++;
            return false;
        }
        chunksVisited++;
        return true;
    }
}
