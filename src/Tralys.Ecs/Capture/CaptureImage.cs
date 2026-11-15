using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tralys.Ecs;

/// <summary>A captured subset of one world, held in memory: component pages per archetype, sparse components, relations, parent links,
/// rows and buffer contents. References, entity fields and the elements of entity buffers alike, are capture-local inside the subset and
/// external when they leave it. Restoring never changes the image, so one image can be restored many times.</summary>
public sealed class CaptureImage
{
    internal CaptureImage(
        Registry registry,
        Entity[] sources,
        CapturePage[] pages,
        SparseRecord[] sparse,
        TagRecord[] tags,
        RelationRecord[] relations,
        ParentLink[] parents,
        RowRecord[] rows,
        byte[][] buffers,
        ExternalRef[] externals
    )
    {
        Registry = registry;
        Sources = sources;
        Pages = pages;
        Sparse = sparse;
        Tags = tags;
        Relations = relations;
        Parents = parents;
        Rows = rows;
        Buffers = buffers;
        Externals = externals;
        for (var i = 0; i < sources.Length; i++)
            locals.Add(sources[i], i);
    }

    readonly Dictionary<Entity, int> locals = [];
    internal readonly Registry Registry;
    internal readonly Entity[] Sources;
    internal readonly CapturePage[] Pages;
    internal readonly SparseRecord[] Sparse;
    internal readonly TagRecord[] Tags;
    internal readonly RelationRecord[] Relations;
    internal readonly ParentLink[] Parents;
    internal readonly RowRecord[] Rows;
    internal readonly byte[][] Buffers;
    internal readonly ExternalRef[] Externals;

    /// <summary>How many entities the image holds: the roots and their captured closure.</summary>
    public int EntityCount => Sources.Length;

    /// <summary>The captured entities' handles in the world they came from, in capture order: roots first, then the closure breadth-first.</summary>
    public ReadOnlySpan<Entity> SourceEntities => Sources;

    /// <summary>The external references the image holds, each once, in the order they were met.</summary>
    public ReadOnlySpan<ExternalRef> ExternalRefs => Externals;

    internal int LocalOf(Entity source) => locals.TryGetValue(source, out var local) ? local : -1;

    /// <summary>Writes the image to <paramref name="stream"/> as bytes: each type it holds by name, schema version and layout, then every
    /// value as stored. <see cref="Read"/> takes it back.</summary>
    /// <exception cref="NotSupportedException">A type the image holds has managed references, so its values have no byte form.</exception>
    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        CaptureFormat.Write(this, stream);
    }

    /// <summary>Reads an image <see cref="Write"/> wrote, to restore into worlds built on <paramref name="registry"/>. Its
    /// <see cref="SourceEntities"/> still name the entities of the world it was captured from.</summary>
    /// <exception cref="InvalidDataException">The stream holds no image, a format version this build cannot read, or a corrupt one; or
    /// it holds a type <paramref name="registry"/> does not declare, declares at another schema version, or lays out differently.</exception>
    public static CaptureImage Read(Stream stream, Registry registry)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(registry);
        return CaptureFormat.Read(stream, registry);
    }

    /// <summary>Component-level differences between two captures, pairing entities by capture order. References compare by what they name
    /// (the same captured position, or the same external reference) and buffer handles by their buffers' contents, the elements of a
    /// <c>BufferHandle&lt;Entity&gt;</c> as references, so two worlds that hand out different handles can still match. Relation edges,
    /// rows and children compare in order. Other fields of a managed component compare by the component's
    /// <see cref="EqualityComparer{T}.Default"/>, so a reference-typed field compares by its type's
    /// <c>Equals</c> (a string by value, a list by identity); implement <see cref="IEquatable{T}"/> on the component to decide otherwise.</summary>
    /// <exception cref="ArgumentException">The images were captured from worlds with different registries.</exception>
    public static IReadOnlyList<WorldDifference> Compare(CaptureImage left, CaptureImage right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Registry != right.Registry)
            throw new ArgumentException(
                "The images were captured from worlds with different registries.",
                nameof(right)
            );
        return new CaptureComparer(left, right).Run();
    }
}

internal sealed class CapturePage(
    bool detached,
    int[] types,
    int[] columnTypes,
    Array[] columns,
    int[] enableTypes,
    bool[] enabled,
    int[] locals,
    bool[] detachedSelf
)
{
    public readonly bool Detached = detached;

    // Per row, on a detached page only: the entity detached itself rather than through an ancestor.
    public readonly bool[] DetachedSelf = detachedSelf;

    public bool DetachedItself(int row) => Detached && DetachedSelf[row];

    public readonly int[] Types = types;
    public readonly int[] ColumnTypes = columnTypes;
    public readonly Array[] Columns = columns;
    public readonly int[] EnableTypes = enableTypes;

    // Indexed row * EnableTypes.Length + slot.
    public readonly bool[] Enabled = enabled;
    public readonly int[] Locals = locals;
}

internal sealed record SparseRecord(int Type, int[] Locals, Array Values);

internal sealed record TagRecord(int Type, int[] Locals);

internal sealed record RelationRecord(int Type, int[] Sources, Entity[] Targets, Array Payloads);

internal readonly record struct ParentLink(int Child, Entity Parent);

internal sealed record RowRecord(int Type, int[] Owners, Array Values);

// Generation 0 marks an image reference: no live handle has it.
internal static class ImageRef
{
    const uint ExternalBit = 0x8000_0000;

    public static Entity Local(int local) => new((uint)local + 1, 0);

    public static Entity External(int index) => Entity.Unchecked(ExternalBit | (uint)index);

    public static bool TryLocal(Entity encoded, out int local)
    {
        local = (int)(encoded.Index - 1);
        return !encoded.IsDefault && (encoded.Index & ExternalBit) == 0;
    }

    public static int ExternalIndex(Entity encoded) => (int)(encoded.Index & ~ExternalBit);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref Entity At(ref byte value, int offset) =>
        ref Unsafe.As<byte, Entity>(ref Unsafe.Add(ref value, offset));

    // Relies on BufferHandle's layout: int slot, then uint generation.
    public static int BufferAt(ref byte value, int offset) =>
        Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref value, offset)) - 1;

    public static void SetBuffer(ref byte value, int offset, int buffer)
    {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref value, offset), buffer + 1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref value, offset + sizeof(int)), 0u);
    }

    public static void ClearBuffer(ref byte value, int offset)
    {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref value, offset), 0);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref value, offset + sizeof(int)), 0u);
    }

    public static Span<Entity> Entities(byte[] contents) =>
        MemoryMarshal.Cast<byte, Entity>(contents.AsSpan());

    public static Span<Entity> Entities(World world, ref byte value, int offset) =>
        world
            .Buffers<Entity>()
            .TryGet(
                Unsafe.ReadUnaligned<BufferHandle<Entity>>(ref Unsafe.Add(ref value, offset)),
                out var held
            )
            ? held
            : default;
}

internal sealed class CapturePlan
{
    public readonly int[] Refs;
    public readonly int[] KeyRefs;
    public readonly int[] Buffers;
    public readonly int[] BufferOps;
    public readonly bool[] EntityBuffers;
    public readonly (int Offset, int Size)[] Skips;
    public readonly FieldDescriptor[] Rebases;

    CapturePlan(ComponentDescriptor descriptor)
    {
        var skips = new List<(int, int)>();
        var rebases = new List<FieldDescriptor>();
        foreach (var field in descriptor.Fields)
        {
            if (field.Capture == CapturePolicy.Skip)
                skips.Add((field.Offset, field.Size));
            else if (field.Capture == CapturePolicy.Rebase)
                rebases.Add(field);
        }
        Skips = [.. skips];
        Rebases = [.. rebases];
        var refs = new List<int>();
        foreach (var offset in descriptor.EntityRefOffsets)
        {
            if (!Skipped(offset))
                refs.Add(offset);
        }
        Refs = [.. refs];
        var key = descriptor.Fields.FirstOrDefault(f => f.Name == descriptor.KeyField);
        KeyRefs = key is null
            ? []
            : [.. refs.Where(offset => offset >= key.Offset && offset < key.Offset + key.Size)];
        var buffers = new List<int>();
        var ops = new List<int>();
        var handles = descriptor.BufferHandleOffsets;
        for (var i = 0; i < handles.Length; i++)
        {
            if (Skipped(handles[i]))
                continue;
            buffers.Add(handles[i]);
            ops.Add(i);
        }
        Buffers = [.. buffers];
        BufferOps = [.. ops];
        EntityBuffers = [.. ops.Select(i => descriptor.BufferOps[i].HoldsEntities)];
    }

    bool Skipped(int offset)
    {
        foreach (var (start, size) in Skips)
        {
            if (offset >= start && offset < start + size)
                return true;
        }
        return false;
    }

    public bool PassesThrough =>
        Refs.Length == 0 && Buffers.Length == 0 && Skips.Length == 0 && Rebases.Length == 0;

    public static CapturePlan For(ComponentDescriptor descriptor) =>
        descriptor.capturePlan ??= new CapturePlan(descriptor);
}
