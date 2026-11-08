namespace Tralys.Ecs;

/// <summary>Declares a data component. Its identity is its fully qualified type name, which must be unique across every module a
/// registry loads.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class ComponentAttribute : Attribute
{
    /// <summary>The layout version layers compare when they read stored data; not a change version.</summary>
    public int SchemaVersion { get; set; } = 1;
}

/// <summary>Declares a zero-size tag. By default it is part of the archetype key, so adding or removing it moves the entity;
/// <see cref="EnableableAttribute"/> makes it a per-row bit instead, and <see cref="StorageKind.Sparse"/> a per-world set for rare
/// markers.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class TagAttribute : Attribute
{
    /// <inheritdoc cref="ComponentAttribute.SchemaVersion"/>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>For an <see cref="EnableableAttribute"/> tag: descendants inherit the effective bit.</summary>
    public bool Inherited { get; set; }
}

/// <summary>Writes to this component carry no change stamps.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class UntrackedAttribute : Attribute;

/// <summary>Where a component or tag is stored; set it with <see cref="StorageAttribute"/>.</summary>
public enum StorageKind
{
    /// <summary>In archetype columns, as part of the archetype key: adding or removing it moves the entity. The default.</summary>
    Archetype,

    /// <summary>In a per-world sparse set: adding or removing it never moves the entity. It cannot be enableable.</summary>
    Sparse,
}

/// <summary>Where a component or tag lives: in archetype columns, or in a per-world sparse set outside the archetype key.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class StorageAttribute(StorageKind kind) : Attribute
{
    /// <summary>Where the component or tag lives.</summary>
    public StorageKind Kind { get; } = kind;
}

/// <summary>The component or tag carries a per-row enable bit; a disabled one reads as absent to queries, single-entity reads, indexes and
/// change samples, and only <see cref="World.HasStored{T}"/> and <see cref="World.TryGetStored{T}"/> see it. It cannot be sparse.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class EnableableAttribute : Attribute;

/// <summary>The component is only ever replaced whole; no <c>ref</c> access is handed out.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class ReplaceOnlyAttribute : Attribute;

/// <summary>At most one entity in a world holds this component.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class SingletonAttribute : Attribute;

/// <summary>Adding the declaring component requires <typeparamref name="T"/> to be stored, enabled or disabled.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class RequiresAttribute<T> : Attribute
    where T : struct, IComponent;

/// <summary>A former name kept for layers that resolve components by name.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class AliasAttribute(string name) : Attribute
{
    /// <summary>The former name.</summary>
    public string Name { get; } = name;
}

/// <summary>How a subset capture (<see cref="World.Capture"/>, <see cref="World.Extract"/>) stores one field; set it with <see cref="CaptureAttribute"/>.</summary>
public enum CapturePolicy
{
    /// <summary>Captured and restored as it is. The default.</summary>
    Copy,

    /// <summary>Captured as default, so it restores as default.</summary>
    Skip,

    /// <summary>Captured as it is, then rewritten at restore by the <see cref="IFieldRebaser"/> the restore is given; the field must be unmanaged.</summary>
    Rebase,
}

/// <summary>Sets the <see cref="CapturePolicy"/> of a field of a declared component, relation or row; a <see cref="RowKeyAttribute"/> field must stay <see cref="CapturePolicy.Copy"/>, and a fixed buffer takes none.</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class CaptureAttribute(CapturePolicy policy) : Attribute
{
    /// <summary>How the field is captured and restored.</summary>
    public CapturePolicy Policy { get; } = policy;
}

/// <summary>Writes to this component raise the descendant version of its ancestors, the related version of relation sources, or both;
/// name at least one.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class PropagateChangesAttribute : Attribute
{
    /// <summary>A write raises <see cref="ChangeTracking.DescendantVersionOf"/> of every ancestor of the written entity.</summary>
    public bool ToAncestors { get; set; }

    /// <summary>A write raises <see cref="ChangeTracking.RelatedVersionOf{R}"/> of every entity with an edge to the written entity.</summary>
    public bool ToRelationSources { get; set; }
}

/// <summary>The kind of a value index.</summary>
public enum IndexKind
{
    /// <summary>Key → the one entity holding it; <see cref="UniqueIndexAttribute"/>.</summary>
    Unique,

    /// <summary>Key → every entity holding it; <see cref="MultiIndexAttribute"/>.</summary>
    Multi,

    /// <summary>Entities in key order; <see cref="OrderedIndexAttribute"/>.</summary>
    Ordered,
}

/// <summary>Which entity a unique index returns for a key two entities share. The other stays recorded, and takes the key when the
/// holder leaves it.</summary>
public enum ConflictPolicy
{
    /// <summary>The earlier holder keeps the key, and <see cref="IndexConflictException"/> is thrown once the world operation that made
    /// the conflict finishes, if the key is still shared then.</summary>
    Throw,

    /// <summary>The earlier holder keeps the key, and nothing throws.</summary>
    KeepExisting,

    /// <summary>The entity that took the key last holds it, and nothing throws.</summary>
    ReplaceExisting,
}

/// <summary>A value index the core keeps current on every write. The key is one field, a tuple of <see cref="Fields"/>, or what
/// <see cref="KeyFrom"/> computes. The generator adds a typed accessor on <see cref="World"/> and on <see cref="IndexLookup{T}"/>,
/// named after the component and <see cref="Name"/>.</summary>
public abstract class IndexAttribute(string[] fields) : Attribute
{
    /// <summary>The names of the instance fields the key is made of, in tuple order; give these or <see cref="KeyFrom"/>, not both.</summary>
    public string[] Fields { get; } = fields;

    /// <summary>A pure static method of the component computing the key from its value: <c>K Of(in T)</c>, or <c>bool TryOf(in T, out K)</c>
    /// returning false to leave the entity out of the index.</summary>
    public string? KeyFrom { get; set; }

    /// <summary>The accessor suffix; defaults to <c>By</c> and the key's field names, or <c>By</c> and <see cref="KeyFrom"/>.</summary>
    public string? Name { get; set; }
}

/// <summary>Key → the one entity holding it.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class UniqueIndexAttribute(params string[] fields) : IndexAttribute(fields)
{
    /// <summary>Which entity holds a key two entities share.</summary>
    public ConflictPolicy OnConflict { get; set; }

    /// <summary>Keys on <c>(parent, key)</c>, so lookups find children of one parent; reparenting moves the entry.</summary>
    public bool PerParent { get; set; }
}

/// <summary>Key → every entity holding it, such as the entities of one map.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class MultiIndexAttribute(params string[] fields) : IndexAttribute(fields)
{
    /// <inheritdoc cref="UniqueIndexAttribute.PerParent"/>
    public bool PerParent { get; set; }
}

/// <summary>Entities in key order, for deadlines and ranges bounded from above; one key field.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class OrderedIndexAttribute(params string[] fields) : IndexAttribute(fields);

/// <summary>Rules for a relation's edges; combine them with <c>|</c>.</summary>
[Flags]
public enum RelationTraits
{
    /// <summary>A source may have any number of targets, and edges are one-way.</summary>
    None = 0,

    /// <summary>A source has at most one target: relating it to another first removes its current edge.</summary>
    Exclusive = 1,

    /// <summary>Relating A to B also relates B to A, and unrelating removes both edges.</summary>
    Symmetric = 2,
}

/// <summary>What destroying an entity does to the relation edges that point at it.</summary>
public enum TargetPolicy
{
    /// <summary>The edges are removed and their sources live on. The default.</summary>
    RemoveEdge,

    /// <summary>Their sources are destroyed too, with <see cref="LifecycleReason.Cascade"/>; leaving through <see cref="World.Extract"/> does not cascade.</summary>
    DestroySource,

    /// <summary>A target cannot be destroyed while a live source that is not destroyed with it points at it. <see cref="World.Destroy"/>,
    /// <see cref="World.SetParent"/> and <see cref="World.Relate{R}"/> throw at the call; finalisation checks again before releasing
    /// anything.</summary>
    Throw,
}

/// <summary>Declares a relation kind; the struct's fields are the edge payload.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class RelationAttribute : Attribute
{
    /// <inheritdoc cref="ComponentAttribute.SchemaVersion"/>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Whether a source holds one target at most, and whether edges mirror.</summary>
    public RelationTraits Traits { get; set; }

    /// <summary>What destroying a target does to the edges pointing at it.</summary>
    public TargetPolicy OnTargetDestroyed { get; set; }
}

/// <summary>Declares keyed per-entity rows; one field carries <see cref="RowKeyAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class RowsAttribute : Attribute
{
    /// <inheritdoc cref="ComponentAttribute.SchemaVersion"/>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Keeps a 64-bit mask of which keys an entity holds, for integer or enum keys 0 to 63; presence checks then skip the row
    /// search.</summary>
    public bool PresenceMask { get; set; }

    /// <summary>Keeps key → owners, so <see cref="World.RowOwners{TKey, TRow}"/> can answer.</summary>
    public bool ReverseKeyIndex { get; set; }
}

/// <summary>The key field of a <see cref="RowsAttribute"/> struct.</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class RowKeyAttribute : Attribute;

/// <summary>A set of components spawned together in one archetype move.</summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class BundleAttribute : Attribute;

/// <summary>Marks a layer's attribute class as a component category; components carrying it get <see cref="Bit"/> in their category mask.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ComponentCategoryAttribute : Attribute
{
    /// <summary>The category's bit in a <see cref="CategoryMask"/>, 0 to 63.</summary>
    public int Bit { get; set; }
}

/// <summary>Marks a layer's attribute class whose uses the generator records, uninterpreted, in component metadata.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MetadataPassthroughAttribute : Attribute;

/// <summary>Declares one closed instantiation of a generic component, so the generator emits its descriptor.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ComponentInstanceAttribute<T> : Attribute
    where T : struct
{
    /// <inheritdoc cref="ComponentAttribute.SchemaVersion"/>
    public int SchemaVersion { get; set; } = 1;
}
