namespace Tralys.Ecs.Generated;

/// <summary>What the generator knows about a type; <see cref="Plumbing.CreateDescriptor{T}"/> adds the runtime layout.</summary>
public sealed class ComponentInfo
{
    /// <summary>Read back through <see cref="ComponentDescriptor.SchemaVersion"/>.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>A hash of the type's layout down through nested structs and buffer elements; a capture's byte form refuses bytes written
    /// under another.</summary>
    public ulong LayoutHash { get; init; }

    /// <summary>Read back through <see cref="ComponentDescriptor.Name"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Read back through <see cref="ComponentDescriptor.Kind"/>.</summary>
    public DescriptorKind Kind { get; init; }

    /// <summary>Read back through <see cref="ComponentDescriptor.Traits"/>.</summary>
    public ComponentTraits Traits { get; init; }

    /// <summary>Read back through <see cref="ComponentDescriptor.Storage"/>.</summary>
    public StorageKind Storage { get; init; }

    /// <summary>Read back through <see cref="ComponentDescriptor.RelationTraits"/>.</summary>
    public RelationTraits RelationTraits { get; init; }

    /// <summary>Read back through <see cref="ComponentDescriptor.OnTargetDestroyed"/>.</summary>
    public TargetPolicy OnTargetDestroyed { get; init; }

    /// <summary>Read back through <see cref="ComponentDescriptor.Categories"/>.</summary>
    public ulong Categories { get; init; }

    /// <summary>Read back through <see cref="ComponentDescriptor.Aliases"/>.</summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>Names of the <see cref="RequiresAttribute{T}"/> targets; the registry resolves them into
    /// <see cref="ComponentDescriptor.Requires"/> and throws when one is missing.</summary>
    public string[] RequiresNames { get; init; } = [];

    /// <summary>Read back through <see cref="ComponentDescriptor.Fields"/>.</summary>
    public FieldDescriptor[] Fields { get; init; } = [];

    /// <summary>Read back through <see cref="ComponentDescriptor.EntityRefOffsets"/>.</summary>
    public int[] EntityRefOffsets { get; init; } = [];

    /// <summary>Read back through <see cref="ComponentDescriptor.BufferHandleOffsets"/>.</summary>
    public int[] BufferHandleOffsets { get; init; } = [];

    /// <summary>One per <see cref="BufferHandleOffsets"/> entry: frees, copies and reallocates that field's buffer.</summary>
    public BufferOps[] BufferOps { get; init; } = [];

    /// <summary>Creates a world's store for a row type.</summary>
    public RowStoreFactory RowFactory { get; init; }

    /// <summary>Read back through <see cref="ComponentDescriptor.Indexes"/>.</summary>
    public IndexDeclaration[] Indexes { get; init; } = [];

    /// <summary>Read back through <see cref="ComponentDescriptor.Passthrough"/>.</summary>
    public PassthroughUse[] Passthrough { get; init; } = [];

    /// <summary>Read back through <see cref="ComponentDescriptor.KeyField"/>.</summary>
    public string? KeyField { get; init; }
}
