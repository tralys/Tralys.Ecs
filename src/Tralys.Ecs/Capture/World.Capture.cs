using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    /// <summary>Captures <paramref name="roots"/> and the closure <paramref name="options"/> follows from them; the default options take
    /// the whole closure with rows and buffer contents. A detached entity enters as a root or as a child of a captured entity, never through
    /// a relation or an entity field; pending-destroy entities never enter;
    /// references to entities left outside become external. No roots give an empty image. The world must be settled.</summary>
    /// <exception cref="ArgumentException">A root is not alive, or the categories keep a component and drop one it requires.</exception>
    [Effect(Effects.Settled)]
    public CaptureImage Capture(ReadOnlySpan<Entity> roots, in CaptureOptions options = default)
    {
        CheckSettled(nameof(Capture));
        return new Capturer(this, options).Run(roots);
    }

    /// <summary>Captures the subset as <see cref="Capture"/> does, then removes it at once. Its entities are logged
    /// <see cref="LogKind.Extracted"/>; destroy and removal observers see <paramref name="reason"/>. Relations from outside into the subset
    /// are removed without applying their <see cref="TargetPolicy"/>: leaving a world is not dying in it. Children the subset leaves behind
    /// are first unparented as <see cref="SetParent(Entity, Entity, int, string, int)"/> would, so an index conflict that causes throws
    /// before anything is removed.</summary>
    /// <exception cref="ArgumentException">A root is not alive, or the categories keep a component and drop one it requires.</exception>
    [Effect(Effects.Settled | Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public ExtractResult Extract(
        ReadOnlySpan<Entity> roots,
        in CaptureOptions options = default,
        LifecycleReason reason = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        CheckSettled(nameof(Extract));
        var image = new Capturer(this, options).Run(roots);
        var sources = image.Sources;
        var left = new List<Entity>();
        foreach (var entity in sources)
        {
            foreach (var child in hierarchy.ChildrenOf(entity))
            {
                if (image.LocalOf(child) < 0)
                    left.Add(child);
            }
        }
        foreach (var child in left)
            Reparent(child, default, -1, reason, guardTargets: false);
        IndexConflictException? conflict;
        var tag = attributed is null ? default : CurrentTag();
        BeginIndexBatch();
        extracting = true;
        try
        {
            foreach (var entity in sources)
                allocator.Slots[entity.Index].DestroyReason = reason;
            if (observers.destroys.Length > 0)
            {
                using var delivery = BeginDelivery();
                var context = new ObserverContext(this, reason);
                List<Exception>? errors = null;
                for (var i = sources.Length - 1; i >= 0; i--)
                {
                    foreach (var (_, observer) in observers.destroys)
                    {
                        try
                        {
                            observer.OnDestroy(context, sources[i]);
                        }
                        catch (Exception error)
                        {
                            (errors ??= []).Add(error);
                        }
                    }
                }
                ObserverRegistry.Rethrow(errors);
            }
            attributed?.BeginReplay(tag);
            for (var i = sources.Length - 1; i >= 0; i--)
                Release(sources[i], reason);
        }
        finally
        {
            attributed?.EndReplay();
            extracting = false;
            conflict = EndIndexBatch();
        }
        return new ExtractResult(image, conflict);
    }
}

internal sealed class Capturer(World world, in CaptureOptions options)
{
    readonly CaptureOptions options = options;
    readonly Registry registry = world.registry;
    readonly List<Entity> order = [];
    readonly Dictionary<Entity, int> locals = [];
    readonly List<ExternalRef> externals = [];
    readonly Dictionary<Entity, int> externalOf = [];
    readonly List<byte[]> buffers = [];
    readonly List<Entity> candidates = [];

    bool Takes(CaptureExclusions part) => (options.Exclude & part) == 0;

    bool Wants(int type) => options.Categories.Matches(registry[type].Categories);

    void CheckCategories()
    {
        if (options.Categories.Bits == 0)
            return;
        foreach (var descriptor in registry.Descriptors)
        {
            if (!options.Categories.Matches(descriptor.Categories))
                continue;
            foreach (var required in descriptor.Requires)
            {
                if (!Wants(required))
                    throw new ArgumentException(
                        $"Capture categories keep {descriptor.Name} but drop {registry[required].Name}, which it requires."
                    );
            }
        }
    }

    public CaptureImage Run(ReadOnlySpan<Entity> roots)
    {
        CheckCategories();
        foreach (var root in roots)
        {
            var state = world.StateOf(root);
            if (state != LifecycleState.Alive)
                throw new ArgumentException(
                    $"Capture root {root} is {state}; a root must be alive."
                );
            Add(root);
        }
        for (var i = 0; i < order.Count; i++)
            Expand(order[i]);
        var sources = order.ToArray();
        return new CaptureImage(
            registry,
            sources,
            Pages(sources),
            Sparse(sources),
            Tags(sources),
            Relations(sources),
            Parents(sources),
            Takes(CaptureExclusions.Rows) ? Rows(sources) : [],
            [.. buffers],
            [.. externals]
        );
    }

    void Add(Entity entity)
    {
        if (locals.TryAdd(entity, order.Count))
            order.Add(entity);
    }

    void Expand(Entity entity)
    {
        candidates.Clear();
        if (Takes(CaptureExclusions.Children))
            candidates.AddRange(world.hierarchy.ChildrenOf(entity));
        var children = candidates.Count;
        var fields = Takes(CaptureExclusions.EntityFields);
        foreach (var store in world.relations)
        {
            if (store is null || !Wants(store.Type))
                continue;
            var edges = store.EdgeCount(entity);
            for (var i = 0; i < edges; i++)
            {
                if (Takes(CaptureExclusions.Relations))
                    candidates.Add(store.EdgeTarget(entity, i));
                if (fields)
                    Gather(store.Type, ref store.EdgePayload(entity, i));
            }
        }
        if (fields)
        {
            ref var slot = ref world.allocator.Slots[entity.Index];
            var archetype = slot.Archetype!;
            for (var i = 0; i < archetype.Types.Length; i++)
            {
                var column = archetype.SlotAt(i).Column;
                if (column >= 0 && Wants(archetype.Types[i]))
                    Gather(archetype.Types[i], ref archetype.RowRef(column, slot.Row));
            }
            if (slot.SparseCount > 0)
            {
                foreach (var storage in world.sparse)
                {
                    if (
                        storage is not null
                        && Wants(storage.Type)
                        && storage.SlotOf(entity.Index) is var at and >= 0
                    )
                        Gather(storage.Type, ref storage.RowRef(at));
                }
            }
            if (Takes(CaptureExclusions.Rows))
            {
                foreach (var store in world.rowStores)
                {
                    if (
                        store is null
                        || !Wants(store.Type)
                        || store.LiveRows(entity) is not (var count and > 0)
                    )
                        continue;
                    var ops = registry[store.Type].Column;
                    var rows = ops.Create(count);
                    store.CopyRows(entity, rows, 0);
                    for (var r = 0; r < count; r++)
                        Gather(store.Type, ref RowBytes.At(rows, r, ops.Size));
                }
            }
        }
        for (var i = 0; i < candidates.Count; i++)
            Consider(candidates[i], child: i < children);
    }

    void Gather(int type, ref byte value)
    {
        var plan = CapturePlan.For(registry[type]);
        foreach (var offset in plan.Refs)
            candidates.Add(ImageRef.At(ref value, offset));
        if (!Takes(CaptureExclusions.Buffers))
            return;
        for (var i = 0; i < plan.Buffers.Length; i++)
        {
            if (plan.EntityBuffers[i])
                candidates.AddRange(ImageRef.Entities(world, ref value, plan.Buffers[i]));
        }
    }

    void Consider(Entity candidate, bool child)
    {
        if (
            candidate.IsDefault
            || locals.ContainsKey(candidate)
            || world.StateOf(candidate) != LifecycleState.Alive
            || world.UnderPendingDestroy(candidate)
            || (!child && world.IsDetached(candidate))
        )
            return;
        if (options.Boundary is { } boundary && !boundary.Include(world, candidate))
            return;
        Add(candidate);
    }

    Entity Encode(Entity entity)
    {
        if (entity.IsDefault || !world.Exists(entity))
            return default;
        if (locals.TryGetValue(entity, out var local))
            return ImageRef.Local(local);
        if (!externalOf.TryGetValue(entity, out var index))
        {
            index = externals.Count;
            externals.Add(
                options.Translator?.Translate(world, entity) ?? new ExternalRef(entity.Bits)
            );
            externalOf.Add(entity, index);
        }
        return ImageRef.External(index);
    }

    // Runs on the image's copy, never on the world's value.
    void Encode(int type, ref byte value)
    {
        var descriptor = registry[type];
        var plan = CapturePlan.For(descriptor);
        if (plan.PassesThrough)
            return;
        foreach (var (offset, size) in plan.Skips)
            Unsafe.InitBlockUnaligned(ref Unsafe.Add(ref value, offset), 0, (uint)size);
        foreach (var offset in plan.Refs)
        {
            ref var reference = ref ImageRef.At(ref value, offset);
            reference = Encode(reference);
        }
        for (var i = 0; i < plan.Buffers.Length; i++)
        {
            var offset = plan.Buffers[i];
            var contents = Takes(CaptureExclusions.Buffers)
                ? descriptor
                    .BufferOps[plan.BufferOps[i]]
                    .Copy(world, ref Unsafe.Add(ref value, offset))
                : null;
            if (contents is null)
                ImageRef.ClearBuffer(ref value, offset);
            else
            {
                if (plan.EntityBuffers[i])
                {
                    foreach (ref var held in ImageRef.Entities(contents))
                        held = Encode(held);
                }
                buffers.Add(contents);
                ImageRef.SetBuffer(ref value, offset, buffers.Count - 1);
            }
        }
    }

    CapturePage[] Pages(Entity[] sources)
    {
        var groups = new Dictionary<Archetype, List<int>>();
        var seen = new List<Archetype>();
        for (var local = 0; local < sources.Length; local++)
        {
            var archetype = world.allocator.Slots[sources[local].Index].Archetype!;
            if (!groups.TryGetValue(archetype, out var members))
            {
                groups.Add(archetype, members = []);
                seen.Add(archetype);
            }
            members.Add(local);
        }
        var pages = new CapturePage[seen.Count];
        for (var p = 0; p < pages.Length; p++)
        {
            var archetype = seen[p];
            var members = groups[archetype];
            var columnTypes = archetype.ColumnTypes.Where(Wants).ToArray();
            var enableTypes = archetype.EnableTypes.Where(Wants).ToArray();
            var columns = new Array[columnTypes.Length];
            var enabled = new bool[members.Count * enableTypes.Length];
            for (var c = 0; c < columnTypes.Length; c++)
            {
                var from = archetype.ColumnSlot(columnTypes[c]);
                var ops = archetype.ColumnOps[from];
                columns[c] = ops.Create(members.Count);
                for (var r = 0; r < members.Count; r++)
                {
                    var row = world.allocator.Slots[sources[members[r]].Index].Row;
                    if (ops.Managed)
                        ops.CopyManaged(archetype.Columns[from], row, columns[c], r);
                    else
                        RowBytes.Copy(archetype.Columns[from], row, columns[c], r, ops.Size);
                    Encode(columnTypes[c], ref RowBytes.At(columns[c], r, ops.Size));
                }
            }
            for (var r = 0; r < members.Count; r++)
            {
                var row = world.allocator.Slots[sources[members[r]].Index].Row;
                for (var e = 0; e < enableTypes.Length; e++)
                    enabled[(r * enableTypes.Length) + e] = archetype.IsEnabled(
                        archetype.EnableSlot(enableTypes[e]),
                        row
                    );
            }
            pages[p] = new CapturePage(
                archetype.Detached,
                [.. archetype.Types.Where(Wants)],
                columnTypes,
                columns,
                enableTypes,
                enabled,
                [.. members],
                archetype.Detached
                    ? [.. members.Select(m => world.allocator.Slots[sources[m].Index].DetachedSelf)]
                    : []
            );
        }
        return pages;
    }

    SparseRecord[] Sparse(Entity[] sources)
    {
        var records = new List<SparseRecord>();
        var holders = new List<int>();
        foreach (var storage in world.sparse)
        {
            if (storage is null || !Wants(storage.Type))
                continue;
            holders.Clear();
            for (var local = 0; local < sources.Length; local++)
            {
                if (
                    world.allocator.Slots[sources[local].Index].SparseCount > 0
                    && storage.Contains(sources[local].Index)
                )
                    holders.Add(local);
            }
            if (holders.Count == 0)
                continue;
            var ops = registry[storage.Type].Column;
            var values = ops.Create(holders.Count);
            for (var k = 0; k < holders.Count; k++)
            {
                ops.Store(values, k, ref storage.RowRef(storage.SlotOf(sources[holders[k]].Index)));
                Encode(storage.Type, ref RowBytes.At(values, k, ops.Size));
            }
            records.Add(new SparseRecord(storage.Type, [.. holders], values));
        }
        return [.. records];
    }

    TagRecord[] Tags(Entity[] sources)
    {
        var records = new List<TagRecord>();
        var holders = new List<int>();
        foreach (var tags in world.sparseTags)
        {
            if (tags is null || !Wants(tags.Type))
                continue;
            holders.Clear();
            for (var local = 0; local < sources.Length; local++)
            {
                if (tags.Contains(sources[local].Index))
                    holders.Add(local);
            }
            if (holders.Count > 0)
                records.Add(new TagRecord(tags.Type, [.. holders]));
        }
        return [.. records];
    }

    RelationRecord[] Relations(Entity[] sources)
    {
        var records = new List<RelationRecord>();
        foreach (var store in world.relations)
        {
            if (store is null || !Wants(store.Type))
                continue;
            var total = 0;
            foreach (var source in sources)
                total += store.EdgeCount(source);
            if (total == 0)
                continue;
            var ops = registry[store.Type].Column;
            var payloads = ops.Create(total);
            var from = new int[total];
            var targets = new Entity[total];
            var k = 0;
            for (var local = 0; local < sources.Length; local++)
            {
                var edges = store.EdgeCount(sources[local]);
                for (var i = 0; i < edges; i++, k++)
                {
                    from[k] = local;
                    targets[k] = Encode(store.EdgeTarget(sources[local], i));
                    ops.Store(payloads, k, ref store.EdgePayload(sources[local], i));
                    Encode(store.Type, ref RowBytes.At(payloads, k, ops.Size));
                }
            }
            records.Add(new RelationRecord(store.Type, from, targets, payloads));
        }
        return [.. records];
    }

    // Restore appends links in sequence, so links to one parent must come in its child order.
    ParentLink[] Parents(Entity[] sources)
    {
        var links = new List<ParentLink>();
        for (var local = 0; local < sources.Length; local++)
        {
            foreach (var child in world.hierarchy.ChildrenOf(sources[local]))
            {
                if (locals.TryGetValue(child, out var captured))
                    links.Add(new ParentLink(captured, ImageRef.Local(local)));
            }
        }
        var outside = new List<Entity>();
        foreach (var source in sources)
        {
            var parent = world.hierarchy.ParentOf(source);
            if (!parent.IsDefault && !locals.ContainsKey(parent) && !outside.Contains(parent))
                outside.Add(parent);
        }
        foreach (var parent in outside)
        {
            var encoded = Encode(parent);
            foreach (var child in world.hierarchy.ChildrenOf(parent))
            {
                if (locals.TryGetValue(child, out var captured))
                    links.Add(new ParentLink(captured, encoded));
            }
        }
        return [.. links];
    }

    RowRecord[] Rows(Entity[] sources)
    {
        var records = new List<RowRecord>();
        foreach (var store in world.rowStores)
        {
            if (store is null || !Wants(store.Type))
                continue;
            var total = 0;
            foreach (var source in sources)
                total += store.LiveRows(source);
            if (total == 0)
                continue;
            var ops = registry[store.Type].Column;
            var values = ops.Create(total);
            var owners = new int[total];
            var k = 0;
            for (var local = 0; local < sources.Length; local++)
            {
                var count = store.LiveRows(sources[local]);
                store.CopyRows(sources[local], values, k);
                for (var r = 0; r < count; r++, k++)
                {
                    owners[k] = local;
                    Encode(store.Type, ref RowBytes.At(values, k, ops.Size));
                }
            }
            records.Add(new RowRecord(store.Type, owners, values));
        }
        return [.. records];
    }
}
