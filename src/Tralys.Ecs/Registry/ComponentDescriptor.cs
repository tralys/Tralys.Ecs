using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tralys.Ecs;

/// <summary>What a descriptor declares.</summary>
public enum DescriptorKind
{
    /// <summary>A data component: <see cref="ComponentAttribute"/>.</summary>
    Component,

    /// <summary>A zero-size tag: <see cref="TagAttribute"/>.</summary>
    Tag,

    /// <summary>A relation kind: <see cref="RelationAttribute"/>.</summary>
    Relation,

    /// <summary>Keyed per-entity rows: <see cref="RowsAttribute"/>.</summary>
    Rows,
}

/// <summary>The declaration options a descriptor carries.</summary>
[Flags]
public enum ComponentTraits
{
    /// <summary>No options.</summary>
    None = 0,

    /// <summary><see cref="UntrackedAttribute"/>: writes carry no change stamps.</summary>
    Untracked = 1 << 0,

    /// <summary><see cref="EnableableAttribute"/>: each row carries an enable bit.</summary>
    Enableable = 1 << 1,

    /// <summary><see cref="ReplaceOnlyAttribute"/>: values are only replaced whole.</summary>
    ReplaceOnly = 1 << 2,

    /// <summary><see cref="SingletonAttribute"/>: at most one entity holds it.</summary>
    Singleton = 1 << 3,

    /// <summary><see cref="PropagateChangesAttribute.ToAncestors"/>.</summary>
    PropagateToAncestors = 1 << 4,

    /// <summary><see cref="PropagateChangesAttribute.ToRelationSources"/>.</summary>
    PropagateToRelationSources = 1 << 5,

    /// <summary><see cref="TagAttribute.Inherited"/>: descendants inherit the effective bit.</summary>
    InheritedTag = 1 << 6,

    /// <summary><see cref="RowsAttribute.PresenceMask"/>.</summary>
    PresenceMask = 1 << 7,

    /// <summary><see cref="RowsAttribute.ReverseKeyIndex"/>.</summary>
    ReverseKeyIndex = 1 << 8,
}

/// <summary>One recorded use of a layer attribute: its full name and constant arguments, uninterpreted. A null name marks a positional argument.</summary>
public sealed record PassthroughUse(string Attribute, object?[] Arguments, string?[] ArgumentNames);

/// <summary>One instance field of a declared type, as the generator laid it out.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Offset">Its byte offset from the start of the value.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="TypeName">Its type, fully qualified, as the generator wrote it.</param>
/// <param name="Capture">How a subset capture stores it.</param>
/// <param name="Passthrough">Its uses of <see cref="MetadataPassthroughAttribute"/> attributes, uninterpreted.</param>
public sealed record FieldDescriptor(
    string Name,
    int Offset,
    int Size,
    string TypeName,
    CapturePolicy Capture,
    PassthroughUse[] Passthrough
);

/// <summary>One <see cref="IndexAttribute"/> of a component, as the generator resolved it.</summary>
public sealed class IndexDeclaration(
    IndexKind kind,
    string name,
    string[] fields,
    string? keyFrom,
    ConflictPolicy onConflict,
    bool perParent,
    IndexFactory factory
)
{
    /// <summary>Unique, multi or ordered.</summary>
    public IndexKind Kind { get; } = kind;

    /// <summary>The accessor suffix, such as <c>ById</c>.</summary>
    public string Name { get; } = name;

    /// <summary>The key's field names in tuple order; empty when <see cref="KeyFrom"/> computes the key.</summary>
    public IReadOnlyList<string> Fields { get; } = fields;

    /// <summary>The static method of the component that computes the key, or null.</summary>
    public string? KeyFrom { get; } = keyFrom;

    /// <summary>Which entity a unique index returns for a key two entities share.</summary>
    public ConflictPolicy OnConflict { get; } = onConflict;

    /// <summary>Whether keys are scoped to the entity's parent.</summary>
    public bool PerParent { get; } = perParent;
    internal IndexFactory Factory { get; } = factory;
}

/// <summary>Runtime metadata of a component, tag, relation or row type. Immutable once its registry is built.</summary>
public sealed class ComponentDescriptor
{
    readonly ComponentInfo info;
    internal readonly ComponentOps Ops;
    internal readonly ColumnOps Column;

    ComponentDescriptor(
        ComponentInfo info,
        ComponentOps ops,
        ColumnOps column,
        int size,
        int alignment,
        bool managed
    )
    {
        this.info = info;
        Ops = ops;
        Column = column;
        Size = size;
        Alignment = alignment;
        IsManaged = managed;
    }

    internal static ComponentDescriptor Create<T>(ComponentInfo info)
        where T : struct
    {
        var managed = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        var size = info.Kind == DescriptorKind.Tag ? 0 : Unsafe.SizeOf<T>();
        return new(
            info,
            ComponentOps.For<T>(),
            ColumnOps<T>.Instance,
            size,
            AlignmentOf<T>(),
            managed
        );
    }

    static int AlignmentOf<T>()
        where T : struct
    {
        var probe = default(AlignmentProbe<T>);
        return (int)
            Unsafe.ByteOffset(
                ref Unsafe.As<AlignmentProbe<T>, byte>(ref probe),
                ref Unsafe.As<T, byte>(ref probe.Value)
            );
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AlignmentProbe<T>
        where T : struct
    {
#pragma warning disable CS0649 // Only its offset is read.
        public byte Pad;
#pragma warning restore CS0649
        public T Value;
    }

    /// <inheritdoc cref="ComponentAttribute.SchemaVersion"/>
    public int SchemaVersion => info.SchemaVersion;

    internal ulong LayoutHash => info.LayoutHash;

    /// <summary>The fully qualified type name, without <c>global::</c>: the type's identity across modules.</summary>
    public string Name => info.Name;

    /// <summary>Whether the type is a component, tag, relation or row type.</summary>
    public DescriptorKind Kind => info.Kind;

    /// <summary>The declaration options the type carries.</summary>
    public ComponentTraits Traits => info.Traits;

    /// <summary>Where values live: archetype columns or a sparse set.</summary>
    public StorageKind Storage => info.Storage;

    /// <summary>For a relation, its edge rules; <see cref="RelationTraits.None"/> otherwise.</summary>
    public RelationTraits RelationTraits => info.RelationTraits;

    /// <summary>For a relation, what destroying a target does to the edges pointing at it.</summary>
    public TargetPolicy OnTargetDestroyed => info.OnTargetDestroyed;

    /// <summary>The <see cref="ComponentCategoryAttribute.Bit"/> of each category attribute the type carries.</summary>
    public ulong Categories => info.Categories;

    /// <summary>Former names, from <see cref="AliasAttribute"/>.</summary>
    public IReadOnlyList<string> Aliases => info.Aliases;

    /// <summary>The instance fields, in declaration order.</summary>
    public IReadOnlyList<FieldDescriptor> Fields => info.Fields;

    /// <summary>Byte offsets of the <see cref="Entity"/> fields, which a restore remaps and the dangling-reference check follows.</summary>
    public ReadOnlySpan<int> EntityRefOffsets => info.EntityRefOffsets;

    /// <summary>Byte offsets of the buffer handle fields, whose buffers are freed and copied with the value.</summary>
    public ReadOnlySpan<int> BufferHandleOffsets => info.BufferHandleOffsets;
    internal ReadOnlySpan<BufferOps> BufferOps => info.BufferOps;
    internal RowStoreFactory RowFactory => info.RowFactory;

    /// <summary>The value indexes declared on the type.</summary>
    public IReadOnlyList<IndexDeclaration> Indexes => info.Indexes;

    /// <summary>Uses of <see cref="MetadataPassthroughAttribute"/> attributes on the type, uninterpreted.</summary>
    public IReadOnlyList<PassthroughUse> Passthrough => info.Passthrough;

    /// <summary>For a row type, the name of its <see cref="RowKeyAttribute"/> field; null otherwise.</summary>
    public string? KeyField => info.KeyField;
    internal ReadOnlySpan<string> RequiresNames => info.RequiresNames;

    /// <summary>The dense index, assigned when the first registry containing this type is built.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>Bytes per value; 0 for a tag.</summary>
    public int Size { get; }

    /// <summary>The alignment of a value in bytes.</summary>
    public int Alignment { get; }

    /// <summary>Whether the type is or contains references.</summary>
    public bool IsManaged { get; }

    /// <summary>Dense indices of the required components, resolved by the registry.</summary>
    public ReadOnlySpan<int> Requires => requires;
    internal int[] requires = [];
    internal CapturePlan? capturePlan;

    /// <summary>Whether the type is a tag.</summary>
    public bool IsTag => info.Kind == DescriptorKind.Tag;

    /// <summary>Whether writes carry change stamps: the type is not <see cref="UntrackedAttribute"/>.</summary>
    public bool IsTracked => (info.Traits & ComponentTraits.Untracked) == 0;

    /// <summary>Whether each row carries an enable bit.</summary>
    public bool IsEnableable => (info.Traits & ComponentTraits.Enableable) != 0;

    /// <summary>Whether values live in a sparse set.</summary>
    public bool IsSparse => info.Storage == StorageKind.Sparse;

    internal bool InSignature =>
        info.Kind is DescriptorKind.Component or DescriptorKind.Tag && !IsSparse;

    internal bool HasColumn => info.Kind == DescriptorKind.Component && !IsSparse;

    /// <inheritdoc/>
    public override string ToString() => Name;
}
