using Microsoft.CodeAnalysis;

namespace Tralys.Ecs.Generators;

static class Diagnostics
{
    const string Declarations = "Tralys.Ecs.Declarations";
    const string Queries = "Tralys.Ecs.Queries";
    const string Scheduling = "Tralys.Ecs.Scheduling";
    const string Usage = "Tralys.Ecs.Usage";

    static DiagnosticDescriptor Make(
        string id,
        string category,
        DiagnosticSeverity severity,
        string title,
        string message,
        params string[] tags
    ) => new(id, title, message, category, severity, isEnabledByDefault: true, customTags: tags);

    public static readonly DiagnosticDescriptor NotPartial = Make(
        "TECS0001",
        Declarations,
        DiagnosticSeverity.Error,
        "Declared type must be partial",
        "'{0}' must be partial so the generator can implement its component interfaces"
    );

    public static readonly DiagnosticDescriptor DuplicateDeclaration = Make(
        "TECS0002",
        Declarations,
        DiagnosticSeverity.Error,
        "Type is declared twice",
        "'{0}' is already declared by {1}; a type name may be declared once across every module a registry loads",
        WellKnownDiagnosticTags.CompilationEnd
    );

    public static readonly DiagnosticDescriptor UndeclaredComponent = Make(
        "TECS0004",
        Declarations,
        DiagnosticSeverity.Error,
        "Component interface implemented by hand",
        "'{0}' implements IComponent by hand; declare it [Component], [Tag], [Relation] or [Rows] and let the generator add the interface"
    );

    public static readonly DiagnosticDescriptor EntityInManagedCollection = Make(
        "TECS0005",
        Declarations,
        DiagnosticSeverity.Error,
        "Entity held where entity-reference offsets cannot reach it",
        "Field '{0}' of '{1}' holds entities in '{2}', where entity-reference offsets cannot reach them; keep them in a BufferHandle<Entity> or a relation"
    );

    public static readonly DiagnosticDescriptor IndexKeyNotComparable = Make(
        "TECS0006",
        Declarations,
        DiagnosticSeverity.Error,
        "Index key type cannot be indexed",
        "The {0} index on '{1}' keys on '{2}', which is not {3}; implement it or key on another field"
    );

    public static readonly DiagnosticDescriptor ReplaceOnlyPropagates = Make(
        "TECS0007",
        Declarations,
        DiagnosticSeverity.Error,
        "ReplaceOnly component propagates changes",
        "'{0}' combines [ReplaceOnly] with [PropagateChanges]; drop one of them"
    );

    public static readonly DiagnosticDescriptor InaccessibleEntityField = Make(
        "TECS0008",
        Declarations,
        DiagnosticSeverity.Error,
        "Entity reference is out of the generator's reach",
        "'{0}' holds an entity or buffer handle in '{1}', which generated code cannot access; make it a field visible to '{2}'"
    );

    public static readonly DiagnosticDescriptor BundleMissingRequirement = Make(
        "TECS0009",
        Declarations,
        DiagnosticSeverity.Error,
        "Bundle lacks a required component",
        "Bundle '{0}' contains '{1}', which requires '{2}'; add a '{2}' field to the bundle"
    );

    public static readonly DiagnosticDescriptor BundleFieldNotComponent = Make(
        "TECS0010",
        Declarations,
        DiagnosticSeverity.Error,
        "Bundle field is not a declared component",
        "Field '{0}' of bundle '{1}' has type '{2}', which is not a declared component or tag; declare the type or drop the field"
    );

    public static readonly DiagnosticDescriptor PlaceholderEscapes = Make(
        "TECS0011",
        Usage,
        DiagnosticSeverity.Error,
        "Placeholder entity escapes its command buffer",
        "The entity created by Commands.Create is {0}, but there it is a placeholder valid only inside its own command buffer; use it within the body, or record what needs it through the same Commands"
    );

    public static readonly DiagnosticDescriptor InvalidQueryHost = Make(
        "TECS0013",
        Queries,
        DiagnosticSeverity.Error,
        "Query is declared where the generator cannot extend it",
        "Query '{0}' must be a non-generic method of a partial class: {1}"
    );

    public static readonly DiagnosticDescriptor QueryWithoutPositiveTerm = Make(
        "TECS0014",
        Queries,
        DiagnosticSeverity.Error,
        "Query has no positive term",
        "Query '{0}' has no read, write, With, AnyOf or Changed term, so it would match every entity; add one"
    );

    public static readonly DiagnosticDescriptor InvalidQueryParameter = Make(
        "TECS0015",
        Queries,
        DiagnosticSeverity.Error,
        "Query parameter is not supported",
        "Parameter '{0}' of query '{1}' is not supported: {2}"
    );

    public static readonly DiagnosticDescriptor RefToUnwritableComponent = Make(
        "TECS0016",
        Queries,
        DiagnosticSeverity.Error,
        "Query takes ref to a component it may not write in place",
        "Parameter '{0}' of query '{1}' takes ref to {2}; take it by in and write it with Set"
    );

    public static readonly DiagnosticDescriptor StructuralWriteInQuery = Make(
        "TECS0045",
        Queries,
        DiagnosticSeverity.Error,
        "Query body or collect predicate changes structure",
        "'{0}' changes structure inside the query '{1}'; {2}"
    );

    public static readonly DiagnosticDescriptor WorldWriteInParallelQuery = Make(
        "TECS0017",
        Queries,
        DiagnosticSeverity.Error,
        "Parallel query body changes the world directly",
        "'{0}' changes the world inside the parallel query '{1}'; {2}"
    );

    public static readonly DiagnosticDescriptor RefParameterNeverWritten = Make(
        "TECS0018",
        Queries,
        DiagnosticSeverity.Warning,
        "Query parameter is taken by ref but never written",
        "Parameter '{0}' of query '{1}' is never written; take it by in"
    );

    public static readonly DiagnosticDescriptor RefAcrossStructuralCall = Make(
        "TECS0019",
        Usage,
        DiagnosticSeverity.Warning,
        "Ref from the world used after a structural change",
        "'{0}' was taken from the world before '{1}' changed its structure, so the ref may point at another entity's row; take it again after the change"
    );

    public static readonly DiagnosticDescriptor InvalidQuery = Make(
        "TECS0020",
        Queries,
        DiagnosticSeverity.Error,
        "Query declaration is invalid",
        "Query '{0}': {1}"
    );

    public static readonly DiagnosticDescriptor SystemNotSystemBase = Make(
        "TECS0021",
        Scheduling,
        DiagnosticSeverity.Error,
        "System does not derive from SystemBase",
        "'{0}' is marked [System] but does not derive from SystemBase; derive it from SystemBase or drop [System]"
    );

    public static readonly DiagnosticDescriptor ReadonlyLookupField = Make(
        "TECS0023",
        Scheduling,
        DiagnosticSeverity.Error,
        "Lookup field is readonly",
        "Lookup field '{0}' of '{1}' is readonly, but the generator assigns it at the start of every run; drop readonly"
    );

    public static readonly DiagnosticDescriptor UnboundLookupField = Make(
        "TECS0025",
        Scheduling,
        DiagnosticSeverity.Error,
        "Lookup field is never bound",
        "Lookup field '{0}' of '{1}' is never bound to a world, because '{1}' is not marked [System]; mark it [System]"
    );

    public static readonly DiagnosticDescriptor InvalidIndex = Make(
        "TECS0026",
        Declarations,
        DiagnosticSeverity.Error,
        "Index declaration is invalid",
        "Index '{0}' on '{1}': {2}"
    );

    public static readonly DiagnosticDescriptor InvalidSet = Make(
        "TECS0029",
        Scheduling,
        DiagnosticSeverity.Error,
        "Set names a type that is not a [SystemSet]",
        "'{0}' names '{1}' as a set, but '{1}' is not a [SystemSet]; mark it [SystemSet] or name one that is"
    );

    public static readonly DiagnosticDescriptor CapturingRunCondition = Make(
        "TECS0030",
        Scheduling,
        DiagnosticSeverity.Error,
        "Run condition can capture state",
        "A run condition must be a static lambda, a static method or a static readonly field, so it cannot capture state"
    );

    public static readonly DiagnosticDescriptor InvalidCapturePolicy = Make(
        "TECS0031",
        Declarations,
        DiagnosticSeverity.Error,
        "Capture policy is invalid",
        "Field '{0}' of '{1}': {2}"
    );

    public static readonly DiagnosticDescriptor UndisposedScope = Make(
        "TECS0032",
        Usage,
        DiagnosticSeverity.Error,
        "Scoped handle is not disposed",
        "The {0} returned by '{1}' is never disposed; declare it with 'using', or {2}"
    );

    public static readonly DiagnosticDescriptor RepeatedViewPart = Make(
        "TECS0034",
        Declarations,
        DiagnosticSeverity.Error,
        "View parts share a name",
        "Part '{0}' of view '{1}' has the same name as another part, so their members would collide; split the parts across two views"
    );

    public static readonly DiagnosticDescriptor PartOutsideView = Make(
        "TECS0035",
        Declarations,
        DiagnosticSeverity.Error,
        "View part on a type that is not a view",
        "[Part<{0}>] on '{1}' does nothing; mark '{1}' [View]"
    );

    public static readonly DiagnosticDescriptor WrongCallSite = Make(
        "TECS0036",
        Usage,
        DiagnosticSeverity.Warning,
        "Caller-info argument records the wrong call site",
        "'{0}' of '{1}' {2}"
    );

    public static readonly DiagnosticDescriptor InvalidPlaybackPoint = Make(
        "TECS0037",
        Scheduling,
        DiagnosticSeverity.Error,
        "Playback point is invalid",
        "Playback point '{0}' of '{1}' {2}"
    );

    public static readonly DiagnosticDescriptor SettledApiInQuery = Make(
        "TECS0038",
        Queries,
        DiagnosticSeverity.Error,
        "Query body calls an API that needs a settled world",
        "'{0}' needs a settled world, so it always throws inside the query '{1}'; call it outside any iteration"
    );

    public static readonly DiagnosticDescriptor RowsWithoutKey = Make(
        "TECS0039",
        Declarations,
        DiagnosticSeverity.Error,
        "Rows type has no row key",
        "'{0}' is declared [Rows] but no field carries [RowKey]; mark the key field [RowKey]"
    );

    public static readonly DiagnosticDescriptor PresenceMaskKey = Make(
        "TECS0040",
        Declarations,
        DiagnosticSeverity.Error,
        "Presence-mask key is out of range",
        "'{0}' sets PresenceMask, so its row key must be an integer or enum with values in 0..63; {1}"
    );

    public static readonly DiagnosticDescriptor MultipleDeclarations = Make(
        "TECS0041",
        Declarations,
        DiagnosticSeverity.Error,
        "Type has more than one declaring attribute",
        "'{0}' carries more than one of [Component], [Tag], [Relation] and [Rows]; keep one"
    );

    public static readonly DiagnosticDescriptor InvalidDeclaration = Make(
        "TECS0042",
        Declarations,
        DiagnosticSeverity.Error,
        "Declaration options conflict or have no effect",
        "'{0}' {1}"
    );

    public static readonly DiagnosticDescriptor OwnerThreadApiInParallelQuery = Make(
        "TECS0043",
        Queries,
        DiagnosticSeverity.Error,
        "Parallel query body uses world scratch",
        "'{0}' uses world scratch, which belongs to the owner thread, so it throws inside the parallel query '{1}'; call it before or after the run"
    );

    public static readonly DiagnosticDescriptor MutationThroughReadonlyReference = Make(
        "TECS0046",
        Usage,
        DiagnosticSeverity.Error,
        "Member that is not readonly called through a readonly reference",
        "'{0}' is not readonly, so through the readonly reference '{1}' it changes a copy and the write is lost; write through Edit, or mark '{0}' readonly if it changes nothing"
    );
}
