using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    /// <summary>Restores a captured subset as new entities: allocated in the image's order, so the same image on two equal worlds gives the
    /// same handles. References inside the subset are remapped and external ones resolved through <see cref="RestoreOptions.Resolver"/>;
    /// a row whose key names an unresolved entity is dropped. The restore is logged and observed like creation, with the options' reason,
    /// and each entity then logs <see cref="LogKind.Restored"/>, which lifecycle observers also see; indexes are updated, and attachments get
    /// <see cref="IWorldAttachment.OnWorldRestored"/>. An index conflict the restore causes is reported in
    /// <see cref="RestoreResult.Conflict"/> rather than thrown, so the caller keeps the mapping; the earlier holder keeps the key.</summary>
    /// <exception cref="ArgumentException">The image comes from a world with another registry.</exception>
    /// <exception cref="InvalidOperationException">Under <see cref="UnresolvedPolicy.Fail"/>, an external reference did not resolve, or a
    /// restored singleton is already held; the world is left unchanged, as it is when the rebaser throws.</exception>
    [Effect(Effects.Settled | Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public RestoreResult Restore(
        CaptureImage image,
        in RestoreOptions options = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Registry != registry)
            throw new ArgumentException(
                "The image was captured from a world with another registry.",
                nameof(image)
            );
        CheckSettled(nameof(Restore));
        var restorer = new Restorer(this, image, options);
        IndexConflictException? conflict;
        BeginIndexBatch();
        try
        {
            restorer.Run();
        }
        finally
        {
            conflict = EndIndexBatch();
        }
        foreach (var attachment in attachmentOrder)
            attachment.OnWorldRestored(
                this,
                new RestoreInfo(RestoreKind.Subset, restorer.Reason, restorer.Created)
            );
        return new RestoreResult(
            image,
            restorer.Created,
            restorer.Unresolved,
            [.. restorer.Deferred],
            conflict
        );
    }
}

internal sealed class Restorer
{
    static readonly LogKinds RestoreReports = LogKinds.Of(LogKind.Added, LogKind.Disabled);

    readonly World world;
    readonly CaptureImage image;
    readonly RestoreOptions options;
    readonly Registry registry;
    readonly Entity[] resolved;
    readonly Array[][] pageColumns;
    readonly Array[] sparseValues;
    readonly Array[] payloads;
    readonly Array[] rowValues;
    public readonly Entity[] Created;
    public readonly LifecycleReason Reason;
    public readonly List<DeferredRef> Deferred = [];
    public int Unresolved;

    // Everything that can throw runs here, before the world changes.
    public Restorer(World world, CaptureImage image, in RestoreOptions options)
    {
        this.world = world;
        this.image = image;
        this.options = options;
        registry = world.registry;
        Reason = options.Reason == default ? LifecycleReason.Restored : options.Reason;
        Created = new Entity[image.Sources.Length];
        resolved = new Entity[image.Externals.Length];
        for (var i = 0; i < resolved.Length; i++)
        {
            if (
                options.Resolver is { } resolver
                && resolver.TryResolve(image.Externals[i], out var entity)
                && world.Exists(entity)
            )
                resolved[i] = entity;
            else if (options.Unresolved == UnresolvedPolicy.Fail)
                throw new InvalidOperationException(
                    $"External reference {image.Externals[i].Value} did not resolve; nothing was restored."
                );
        }
        CheckSingletons();
        pageColumns = new Array[image.Pages.Length][];
        for (var p = 0; p < pageColumns.Length; p++)
        {
            var page = image.Pages[p];
            pageColumns[p] = new Array[page.Columns.Length];
            for (var c = 0; c < page.Columns.Length; c++)
                pageColumns[p][c] = Rebased(
                    page.ColumnTypes[c],
                    page.Columns[c],
                    page.Locals.Length
                );
        }
        sparseValues = [.. image.Sparse.Select(r => Rebased(r.Type, r.Values, r.Locals.Length))];
        payloads = [.. image.Relations.Select(r => Rebased(r.Type, r.Payloads, r.Sources.Length))];
        rowValues = [.. image.Rows.Select(r => Rebased(r.Type, r.Values, r.Owners.Length))];
    }

    Array Rebased(int type, Array values, int count)
    {
        var plan = CapturePlan.For(registry[type]);
        if (options.Rebaser is not { } rebaser || plan.Rebases.Length == 0)
            return values;
        var size = registry[type].Column.Size;
        var copy = (Array)values.Clone();
        for (var i = 0; i < count; i++)
        {
            ref var value = ref RowBytes.At(copy, i, size);
            foreach (var field in plan.Rebases)
                rebaser.Rebase(
                    field,
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref value, field.Offset), field.Size)
                );
        }
        return copy;
    }

    void CheckSingletons()
    {
        foreach (var page in image.Pages)
        {
            foreach (var type in page.Types)
                CheckSingleton(type);
        }
        foreach (var record in image.Sparse)
            CheckSingleton(record.Type);
        foreach (var record in image.Tags)
            CheckSingleton(record.Type);
    }

    void CheckSingleton(int type)
    {
        var slot = world.layout.SingletonSlot[type];
        if (slot >= 0 && world.allocator.IsCurrent(world.singletonHolders[slot]))
            throw new InvalidOperationException(
                $"{registry[type].Name} is a singleton this world already holds; nothing was restored."
            );
    }

    public void Run()
    {
        for (var local = 0; local < Created.Length; local++)
            Created[local] = world.allocator.Allocate(LifecycleState.Alive);
        for (var p = 0; p < image.Pages.Length; p++)
            RestorePage(image.Pages[p], pageColumns[p]);
        for (var i = 0; i < image.Sparse.Length; i++)
            RestoreSparse(image.Sparse[i], sparseValues[i]);
        foreach (var record in image.Tags)
            RestoreTags(record);
        foreach (var link in image.Parents)
        {
            var child = Created[link.Child];
            var parent = Decode(link.Parent, child, -1, -1, -1);
            if (!parent.IsDefault)
                world.Reparent(child, parent, -1, Reason, guardTargets: false);
        }
        if (world.partitionsUsed)
        {
            foreach (var entity in Created)
            {
                if (world.hierarchy.ParentOf(entity).IsDefault)
                    world.RefreshPartitions(entity, Reason);
            }
        }
        for (var i = 0; i < image.Relations.Length; i++)
            RestoreRelations(image.Relations[i], payloads[i]);
        for (var i = 0; i < image.Rows.Length; i++)
            RestoreRows(image.Rows[i], rowValues[i]);
        foreach (var entity in Created)
        {
            world.RefreshAllIndexes(entity);
            world.changes.Append(LogKind.Restored, entity, -1, Reason);
            world.observers.QueueLifecycle(entity, LogKind.Restored, Reason);
        }
    }

    void RestorePage(CapturePage page, Array[] columns)
    {
        var target = world.ArchetypeFor(world.root.Signature.WithAll(page.Types, page.Detached));
        var hearAdds = world.observers.AnyAdd;
        var logged = hearAdds || world.changes.WantsAny(RestoreReports);
        for (var r = 0; r < page.Locals.Length; r++)
        {
            var entity = Created[page.Locals[r]];
            ref var slot = ref world.allocator.Slots[entity.Index];
            world.Place(entity, ref slot, target);
            world.changes.Append(LogKind.Created, entity, -1, Reason);
            if (page.Detached)
            {
                slot.DetachedSelf = page.DetachedSelf[r];
                world.partitionsUsed = true;
                world.changes.Append(LogKind.Detached, entity, -1, Reason);
            }
            var row = slot.Row;
            for (var c = 0; c < page.ColumnTypes.Length; c++)
            {
                var column = target.ColumnSlot(page.ColumnTypes[c]);
                var ops = target.ColumnOps[column];
                if (ops.Managed)
                    ops.CopyManaged(columns[c], r, target.Columns[column], row);
                else
                    RowBytes.Copy(columns[c], r, target.Columns[column], row, ops.Size);
                Patch(page.ColumnTypes[c], ref target.RowRef(column, row), entity, -1);
            }
            for (var e = 0; e < page.EnableTypes.Length; e++)
                target.SetEnabled(
                    target.EnableSlot(page.EnableTypes[e]),
                    row,
                    page.Enabled[(r * page.EnableTypes.Length) + e]
                );
            if (target.HasSingleton)
            {
                foreach (var type in page.Types)
                    world.ClaimSingleton(entity, type);
            }
            if (world.Tracking)
                target.StampAddedAll(row, world.version);
            if (!logged)
                continue;
            foreach (var type in page.Types)
            {
                if (!hearAdds && !world.changes.Wants(type, RestoreReports))
                    continue;
                var enable = target.EnableSlot(type);
                var column = target.ColumnSlot(type);
                world.Report(
                    ComponentChange.Of(
                        false,
                        false,
                        true,
                        enable < 0 || target.IsEnabled(enable, row)
                    ),
                    entity,
                    type,
                    Reason,
                    ref Unsafe.NullRef<byte>(),
                    ref column >= 0 ? ref target.RowRef(column, row) : ref Unsafe.NullRef<byte>()
                );
            }
        }
    }

    void RestoreSparse(SparseRecord record, Array values)
    {
        var storage = world.SparseFor(record.Type);
        var size = registry[record.Type].Column.Size;
        var tracked = world.Tracking && registry[record.Type].IsStamped;
        for (var k = 0; k < record.Locals.Length; k++)
        {
            var entity = Created[record.Locals[k]];
            var at = storage.AddFrom(entity, ref RowBytes.At(values, k, size));
            Patch(record.Type, ref storage.RowRef(at), entity, -1);
            world.allocator.Slots[entity.Index].SparseCount++;
            world.ClaimSingleton(entity, record.Type);
            if (tracked)
                storage.StampAdded(at, world.version);
            world.Report(
                ComponentChange.Added,
                entity,
                record.Type,
                Reason,
                ref Unsafe.NullRef<byte>(),
                ref storage.RowRef(at)
            );
        }
    }

    void RestoreTags(TagRecord record)
    {
        ref var tags = ref world.sparseTags[world.layout.SparseTagSlot[record.Type]];
        tags ??= new SparseTagStorage(record.Type);
        foreach (var local in record.Locals)
        {
            var entity = Created[local];
            if (tags.Set(entity.Index, true))
                world.allocator.Slots[entity.Index].SparseCount++;
            world.ClaimSingleton(entity, record.Type);
            world.Report(
                ComponentChange.Added,
                entity,
                record.Type,
                Reason,
                ref Unsafe.NullRef<byte>(),
                ref Unsafe.NullRef<byte>()
            );
        }
    }

    void RestoreRelations(RelationRecord record, Array values)
    {
        var store = world.RelationsOf(record.Type);
        var ops = registry[record.Type].Column;
        var payload = ops.Create(1);
        var edge = 0;
        for (var k = 0; k < record.Sources.Length; k++)
        {
            edge = k > 0 && record.Sources[k] == record.Sources[k - 1] ? edge + 1 : 0;
            var source = Created[record.Sources[k]];
            var target = Decode(record.Targets[k], source, record.Type, edge, -1);
            if (target.IsDefault)
                continue;
            Stage(ops, values, k, payload);
            Patch(record.Type, ref RowBytes.At(payload, 0, ops.Size), source, edge);
            store.ApplyRelate(world, source, target, ref RowBytes.At(payload, 0, ops.Size), Reason);
        }
    }

    void RestoreRows(RowRecord record, Array values)
    {
        var store = world.RowsOf(record.Type);
        var ops = registry[record.Type].Column;
        var row = ops.Create(1);
        var position = 0;
        for (var k = 0; k < record.Owners.Length; k++)
        {
            position = k > 0 && record.Owners[k] == record.Owners[k - 1] ? position + 1 : 0;
            var owner = Created[record.Owners[k]];
            Stage(ops, values, k, row);
            if (Patch(record.Type, ref RowBytes.At(row, 0, ops.Size), owner, position))
                store.ApplyUpsert(world, owner, ref RowBytes.At(row, 0, ops.Size), Reason);
        }
    }

    static void Stage(ColumnOps ops, Array from, int index, Array into)
    {
        if (ops.Managed)
            ops.CopyManaged(from, index, into, 0);
        else
            RowBytes.Copy(from, index, into, 0, ops.Size);
    }

    // Runs on the world's copy; the image must keep its encoded form.
    bool Patch(int type, ref byte value, Entity owner, int row)
    {
        var descriptor = registry[type];
        var plan = CapturePlan.For(descriptor);
        if (plan.PassesThrough)
            return true;
        var keyed = true;
        foreach (var offset in plan.Refs)
        {
            ref var reference = ref ImageRef.At(ref value, offset);
            var encoded = reference;
            reference = Decode(encoded, owner, type, row, offset);
            if (
                reference.IsDefault
                && !encoded.IsDefault
                && Array.IndexOf(plan.KeyRefs, offset) >= 0
            )
                keyed = false;
        }
        if (!keyed)
            return false;
        for (var i = 0; i < plan.Buffers.Length; i++)
        {
            var offset = plan.Buffers[i];
            var buffer = ImageRef.BufferAt(ref value, offset);
            if (buffer < 0)
            {
                ImageRef.ClearBuffer(ref value, offset);
                continue;
            }
            descriptor
                .BufferOps[plan.BufferOps[i]]
                .Allocate(world, image.Buffers[buffer], ref Unsafe.Add(ref value, offset));
            if (!plan.EntityBuffers[i])
                continue;
            var held = ImageRef.Entities(world, ref value, offset);
            for (var e = 0; e < held.Length; e++)
                held[e] = Decode(held[e], owner, type, row, offset, e);
        }
        return true;
    }

    Entity Decode(Entity encoded, Entity owner, int type, int row, int offset, int element = -1)
    {
        if (encoded.IsDefault)
            return default;
        if (ImageRef.TryLocal(encoded, out var local))
            return Created[local];
        var index = ImageRef.ExternalIndex(encoded);
        if (!resolved[index].IsDefault)
            return resolved[index];
        Unresolved++;
        if (options.Unresolved == UnresolvedPolicy.Defer)
            Deferred.Add(
                new DeferredRef(owner, type, row, offset, element, image.Externals[index])
            );
        return default;
    }
}
