using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>Which kind of stored value holds a <see cref="DanglingReference"/>.</summary>
public enum ReferenceHolder : byte
{
    /// <summary>A component value.</summary>
    Component,

    /// <summary>The payload of a relation edge.</summary>
    RelationPayload,

    /// <summary>A keyed row.</summary>
    Row,

    /// <summary>The target of a relation edge; <see cref="DanglingReference.Offset"/> and <see cref="DanglingReference.Element"/> are
    /// -1.</summary>
    RelationTarget,
}

/// <summary>An entity reference stored in <paramref name="Holder"/>'s <paramref name="Type"/> value, at <paramref name="Offset"/> bytes
/// inside the field named <paramref name="Field"/>, that names the dead entity <paramref name="Target"/>. For an element of a
/// <c>BufferHandle&lt;Entity&gt;</c>, <paramref name="Offset"/> is the handle's and <paramref name="Element"/> the element's index in the
/// buffer; otherwise <paramref name="Element"/> is -1.</summary>
public readonly record struct DanglingReference(
    Entity Holder,
    ComponentDescriptor Type,
    ReferenceHolder Kind,
    int Offset,
    int Element,
    string? Field,
    Entity Target
)
{
    /// <summary><c>Holder's Type.Field[Element] (offset Offset) names dead Target</c>, without <c>[Element]</c> outside a
    /// buffer.</summary>
    public override string ToString() =>
        $"{Holder}'s {Type.Name}{(Field is null ? "" : "." + Field)}{(Element < 0 ? "" : $"[{Element}]")} (offset {Offset}) names dead {Target}";
}

/// <summary>Receives the dangling references <see cref="WorldOptions.VerifyReferences"/> finds. Without a sink they throw.</summary>
public interface IReferenceFaultSink
{
    /// <summary>Called once per dangling reference found after a playback; a playback the sink runs is not verified.</summary>
    [Effect(
        Effects.Structural
            | Effects.MovesEntities
            | Effects.Mutates
            | Effects.OwnerThread
            | Effects.MovesRowBlocks
    )]
    void OnDanglingReference(in DanglingReference reference);
}

/// <summary>Thrown after a playback when <see cref="WorldOptions.VerifyReferences"/> finds dangling references and no
/// <see cref="World.ReferenceFaults"/> sink is set; the playback itself completed.</summary>
public sealed class DanglingReferenceException(DanglingReference first, int count)
    : InvalidOperationException(
        count == 1 ? $"{first}." : $"{first}, and {count - 1} more dangling references."
    )
{
    /// <summary>The first dangling reference found.</summary>
    public DanglingReference First { get; } = first;

    /// <summary>How many dangling references the walk found.</summary>
    public int Count { get; } = count;
}

public sealed partial class World
{
    readonly bool verifyReferences;
    bool verifying;

    /// <summary>Receives what <see cref="WorldOptions.VerifyReferences"/> finds after each playback; a playback the sink runs is not
    /// verified again. Null throws a <see cref="DanglingReferenceException"/> instead, once the playback has finished, and again after
    /// every playback until the references are fixed.</summary>
    public IReferenceFaultSink? ReferenceFaults { get; set; }

    DanglingReferenceException? VerifyAfterPlayback()
    {
        if (verifying)
            return null;
        var found = new List<DanglingReference>();
        if (FindDanglingReferences(found) == 0)
            return null;
        if (ReferenceFaults is not { } sink)
            return new DanglingReferenceException(found[0], found.Count);
        verifying = true;
        try
        {
            foreach (var reference in found)
                sink.OnDanglingReference(reference);
        }
        finally
        {
            verifying = false;
        }
        return null;
    }

    /// <summary>Adds every stored entity reference that names a dead entity to <paramref name="into"/>, and returns how many it added.
    /// Walks relation targets and every component value, relation payload and row whose type holds references, including inline-array
    /// elements, nested fields and the elements of entity buffers.</summary>
    [Effect(Effects.None)]
    public int FindDanglingReferences(ICollection<DanglingReference> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        var before = into.Count;
        foreach (var archetype in archetypes)
        {
            for (var c = 0; c < archetype.ColumnTypes.Length; c++)
            {
                var descriptor = registry[archetype.ColumnTypes[c]];
                if (!HoldsReferences(descriptor))
                    continue;
                for (var row = 0; row < archetype.Count; row++)
                    Check(
                        archetype.Entities[row],
                        descriptor,
                        ReferenceHolder.Component,
                        ref archetype.RowRef(c, row),
                        into
                    );
            }
        }
        foreach (var storage in sparse)
        {
            if (storage is null)
                continue;
            var descriptor = registry[storage.Type];
            if (!HoldsReferences(descriptor))
                continue;
            for (var i = 0; i < storage.Count; i++)
                Check(
                    storage.Owners[i],
                    descriptor,
                    ReferenceHolder.Component,
                    ref storage.RowRef(i),
                    into
                );
        }
        foreach (var store in relations)
        {
            if (store is null)
                continue;
            var descriptor = registry[store.Type];
            var payloads = HoldsReferences(descriptor);
            foreach (var archetype in archetypes)
            {
                for (var row = 0; row < archetype.Count; row++)
                {
                    var source = archetype.Entities[row];
                    var edges = store.EdgeCount(source);
                    for (var edge = 0; edge < edges; edge++)
                    {
                        var target = store.EdgeTarget(source, edge);
                        if (!allocator.IsCurrent(target))
                            into.Add(
                                new DanglingReference(
                                    source,
                                    descriptor,
                                    ReferenceHolder.RelationTarget,
                                    -1,
                                    -1,
                                    null,
                                    target
                                )
                            );
                        if (payloads)
                            Check(
                                source,
                                descriptor,
                                ReferenceHolder.RelationPayload,
                                ref store.EdgePayload(source, edge),
                                into
                            );
                    }
                }
            }
        }
        foreach (var store in rowStores)
        {
            if (store is null || !HoldsReferences(registry[store.Type]))
                continue;
            var descriptor = registry[store.Type];
            foreach (var archetype in archetypes)
            {
                for (var row = 0; row < archetype.Count; row++)
                {
                    var owner = archetype.Entities[row];
                    var slots = store.SlotCount(owner);
                    for (var slot = 0; slot < slots; slot++)
                    {
                        ref var value = ref store.SlotRef(owner, slot);
                        if (!Unsafe.IsNullRef(ref value))
                            Check(owner, descriptor, ReferenceHolder.Row, ref value, into);
                    }
                }
            }
        }
        return into.Count - before;
    }

    static bool HoldsReferences(ComponentDescriptor descriptor)
    {
        if (!descriptor.EntityRefOffsets.IsEmpty)
            return true;
        foreach (var ops in descriptor.BufferOps)
        {
            if (ops.HoldsEntities)
                return true;
        }
        return false;
    }

    void Check(
        Entity holder,
        ComponentDescriptor descriptor,
        ReferenceHolder kind,
        ref byte value,
        ICollection<DanglingReference> into
    )
    {
        foreach (var offset in descriptor.EntityRefOffsets)
        {
            var target = Unsafe.ReadUnaligned<Entity>(ref Unsafe.Add(ref value, offset));
            if (!target.IsDefault && !allocator.IsCurrent(target))
                into.Add(
                    new DanglingReference(
                        holder,
                        descriptor,
                        kind,
                        offset,
                        -1,
                        FieldAt(descriptor, offset),
                        target
                    )
                );
        }
        var handles = descriptor.BufferHandleOffsets;
        var ops = descriptor.BufferOps;
        for (var i = 0; i < handles.Length; i++)
        {
            if (!ops[i].HoldsEntities)
                continue;
            var held = ImageRef.Entities(this, ref value, handles[i]);
            for (var e = 0; e < held.Length; e++)
            {
                if (!held[e].IsDefault && !allocator.IsCurrent(held[e]))
                    into.Add(
                        new DanglingReference(
                            holder,
                            descriptor,
                            kind,
                            handles[i],
                            e,
                            FieldAt(descriptor, handles[i]),
                            held[e]
                        )
                    );
            }
        }
    }

    static string? FieldAt(ComponentDescriptor descriptor, int offset)
    {
        foreach (var field in descriptor.Fields)
        {
            if (offset >= field.Offset && offset < field.Offset + field.Size)
                return field.Name;
        }
        return null;
    }
}
