using Tralys.Ecs.Generators.CodeGen;

namespace Tralys.Ecs.Generators.Metadata;

enum DeclKind
{
    Component,
    Tag,
    Relation,
    Rows,
}

sealed record ContainerModel(string Keyword, string Name);

sealed record PassthroughModel(
    string Attribute,
    EquatableArray<string> Arguments,
    EquatableArray<string> Names
);

sealed record FieldModel(
    string Name,
    string Access,
    string Type,
    int Capture,
    EquatableArray<PassthroughModel> Passthrough
);

sealed record RefModel(string Access, string Type, string? Guard);

sealed record IndexModel(
    int Ordinal,
    int Kind,
    string Name,
    EquatableArray<string> Fields,
    string? KeyFrom,
    int OnConflict,
    bool PerParent,
    string KeyType,
    string KeyExpression,
    string? TryCall
);

sealed record TypeModel(
    string Namespace,
    EquatableArray<ContainerModel> Containers,
    string DeclarationName,
    string Keyword,
    string FullName,
    string DisplayName,
    bool IsGenericDefinition,
    DeclKind Kind,
    int Version,
    int Traits,
    int Storage,
    int RelationTraits,
    int OnTargetDestroyed,
    ulong Categories,
    EquatableArray<string> Aliases,
    EquatableArray<string> RequiresNames,
    EquatableArray<FieldModel> Fields,
    EquatableArray<RefModel> EntityRefs,
    EquatableArray<RefModel> BufferHandles,
    EquatableArray<IndexModel> Indexes,
    string AccessorVisibility,
    EquatableArray<PassthroughModel> Passthrough,
    string? KeyField,
    string? KeyType,
    ulong Layout,
    EquatableArray<Problem> Problems
)
{
    public bool CanEmit => Problems.Items.IsEmpty;
}

sealed record InstanceModel(
    string FullName,
    string DisplayName,
    int Version,
    TypeModel? Definition,
    EquatableArray<RefModel> EntityRefs,
    EquatableArray<RefModel> BufferHandles,
    ulong Layout,
    EquatableArray<Problem> Problems
);

sealed record BundleMember(string Field, string Type, bool IsTag);

sealed record BundleModel(
    string Namespace,
    EquatableArray<ContainerModel> Containers,
    string DeclarationName,
    string Keyword,
    string FullName,
    EquatableArray<BundleMember> Members,
    EquatableArray<Problem> Problems
);

static class Traits
{
    public const int Tracked = 1 << 0;
    public const int Enableable = 1 << 1;
    public const int ReplaceOnly = 1 << 2;
    public const int Singleton = 1 << 3;
    public const int PropagateToAncestors = 1 << 4;
    public const int PropagateToRelationSources = 1 << 5;
    public const int InheritedTag = 1 << 6;
    public const int PresenceMask = 1 << 7;
    public const int ReverseKeyIndex = 1 << 8;
}
