using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Metadata;

namespace Tralys.Ecs.Generators.Queries;

enum ParamKind
{
    Entity,
    In,
    Ref,
    Optional,
    OptionalRef,
    Commands,
    Context,
    World,
    Reader,
    Arg,
    Accumulator,
    Parent,
    Up,
}

// Names match the QueryDescBuilder methods the emitter calls.
enum TermKind
{
    Read,
    Write,
    Optional,
    OptionalWrite,
    With,
    Without,
    AnyOf,
    Changed,
}

enum ActArgKind
{
    Entity,
    WorldEntity,
    World,
    Arg,
}

sealed record ParamModel(
    string Name,
    ParamKind Kind,
    string Type,
    int Term,
    bool Sparse,
    string Modifier,
    bool RefStruct,
    bool StampOnly
);

sealed record TermModel(string Type, TermKind Kind, int Group, bool Sparse);

sealed record ActArgModel(ActArgKind Kind, string Name, string Modifier);

sealed record HostLookup(string Name, bool IsStatic);

sealed record QueryModel(
    string Namespace,
    EquatableArray<ContainerModel> Containers,
    string TypeKeyword,
    string TypeName,
    string TypeHint,
    bool IsSystem,
    string Method,
    string Accessibility,
    bool IsStatic,
    bool Parallel,
    bool DepthOrdered,
    bool ReturnsBool,
    bool IsCollect,
    string? Act,
    bool ActStatic,
    EquatableArray<ActArgModel> ActArgs,
    string? Accumulator,
    EquatableArray<ParamModel> Params,
    EquatableArray<TermModel> Terms,
    bool IncludePending,
    bool IncludeDetached,
    EquatableArray<HostLookup> Lookups,
    EquatableArray<Problem> Problems
)
{
    public bool CanEmit => Problems.Items.IsEmpty;

    public bool RunStatic => IsStatic && (!IsCollect || ActStatic);
}
