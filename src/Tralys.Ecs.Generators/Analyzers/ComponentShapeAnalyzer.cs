using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Tralys.Ecs.Generators.Indexes;
using Tralys.Ecs.Generators.Metadata;

namespace Tralys.Ecs.Generators.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ComponentShapeAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [
            Diagnostics.UndeclaredComponent,
            Diagnostics.EntityInManagedCollection,
            Diagnostics.IndexKeyNotComparable,
            Diagnostics.ReplaceOnlyPropagates,
            Diagnostics.InvalidCapturePolicy,
            Diagnostics.InvalidDeclaration,
            Diagnostics.PartOutsideView,
        ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (!Effects.IsRuntime(start.Compilation))
                start.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
        });
    }

    static void Analyze(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var location = type.Locations.FirstOrDefault() ?? Location.None;
        var declared = Symbols.IsDeclared(type);
        CheckCapturePolicies(context, type, declared);
        CheckViewParts(context, type, location);

        if (!declared && type.AllInterfaces.Any(i => Symbols.Is(i, Symbols.IComponent)))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.UndeclaredComponent, location, Symbols.Name(type))
            );
            return;
        }
        if (!declared)
            return;

        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.Type.IsValueType)
                continue;
            if (LayoutWalker.ContainsEntity(field.Type, 0))
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.EntityInManagedCollection,
                        field.Locations.FirstOrDefault() ?? location,
                        field.Name,
                        Symbols.Name(type),
                        Symbols.Name(field.Type)
                    )
                );
        }

        var replaceOnly = false;
        var tracked = false;
        var relation = false;
        var propagates = false;
        var directed = false;
        var tag = false;
        var rows = false;
        var inherited = false;
        var enableable = false;
        var sparse = false;
        foreach (var attribute in type.GetAttributes())
        {
            if (Symbols.IsAttribute(attribute, "Tralys.Ecs.ReplaceOnlyAttribute"))
                replaceOnly = true;
            else if (Symbols.IsAttribute(attribute, "Tralys.Ecs.TrackedAttribute"))
                tracked = true;
            else if (Symbols.IsAttribute(attribute, Symbols.Relation))
                relation = true;
            else if (Symbols.IsAttribute(attribute, "Tralys.Ecs.PropagateChangesAttribute"))
            {
                propagates = true;
                directed =
                    Symbols.Named(attribute, "ToAncestors", false)
                    || Symbols.Named(attribute, "ToRelationSources", false);
            }
            else if (Symbols.IsAttribute(attribute, Symbols.Tag))
            {
                tag = true;
                inherited = Symbols.Named(attribute, "Inherited", false);
            }
            else if (Symbols.IsAttribute(attribute, Symbols.Rows))
                rows = true;
            else if (Symbols.IsAttribute(attribute, "Tralys.Ecs.EnableableAttribute"))
                enableable = true;
            else if (Symbols.IsAttribute(attribute, "Tralys.Ecs.StorageAttribute"))
                sparse = attribute.ConstructorArguments.FirstOrDefault().Value is 1;
            else if (Symbols.IndexKindOf(attribute) is not null)
                CheckIndex(context, type, attribute, location);
        }
        if (replaceOnly && propagates)
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.ReplaceOnlyPropagates, location, Symbols.Name(type))
            );
        string? invalid =
            enableable && sparse
                ? "is enableable and sparse, but a sparse set has no per-row enable bit; drop [Enableable] or [Storage(StorageKind.Sparse)]"
            : propagates && (rows || relation)
                ? $"has [PropagateChanges], which only a component's writes raise; drop it from the {(rows ? "rows" : "relation")} type"
            : propagates && !directed
                ? "has [PropagateChanges] with no direction; set ToAncestors, ToRelationSources or both"
            : tag && inherited && !enableable
                ? "sets Inherited, which only an enableable tag uses; add [Enableable] or drop Inherited"
            : tracked && (relation || (tag && (sparse || !enableable)))
                ? "is [Tracked], but a relation, a sparse tag or a tag that is not enableable has no change stamps; drop [Tracked] and watch it with Relation or Presence"
            : null;
        if (invalid is not null)
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.InvalidDeclaration,
                    location,
                    Symbols.Name(type),
                    invalid
                )
            );
        if (rows)
            return;
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (Symbols.Has(field, "Tralys.Ecs.RowKeyAttribute"))
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.InvalidDeclaration,
                        field.Locations.FirstOrDefault() ?? location,
                        Symbols.Name(type),
                        $"marks '{field.Name}' [RowKey], which only a [Rows] struct reads; declare the type [Rows] or drop [RowKey]"
                    )
                );
        }
    }

    static void CheckViewParts(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        Location location
    )
    {
        if (Symbols.Has(type, Symbols.View))
            return;
        foreach (var attribute in type.GetAttributes())
        {
            if (
                Symbols.IsAttribute(attribute, Symbols.Part)
                && attribute.AttributeClass is { TypeArguments.Length: 1 } part
            )
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.PartOutsideView,
                        attribute
                            .ApplicationSyntaxReference?.GetSyntax(context.CancellationToken)
                            .GetLocation()
                            ?? location,
                        Symbols.Name(part.TypeArguments[0]),
                        Symbols.Name(type)
                    )
                );
        }
    }

    static void CheckCapturePolicies(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        bool declared
    )
    {
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            var capture = field
                .GetAttributes()
                .FirstOrDefault(a => Symbols.IsAttribute(a, "Tralys.Ecs.CaptureAttribute"));
            if (capture?.ConstructorArguments.FirstOrDefault().Value is not int policy)
                continue;
            var problem =
                !declared
                    ? "a capture policy applies only to fields of a declared component, row or relation, so here it has no effect"
                : field.IsFixedSizeBuffer
                    ? "a fixed buffer cannot carry a capture policy; wrap it in an inline array"
                : policy != 0 && Symbols.Has(field, "Tralys.Ecs.RowKeyAttribute")
                    ? "a row key must be captured as it is, or restored rows would collide"
                : policy == 2 && !field.Type.IsUnmanagedType
                    ? "a Rebase field must be unmanaged, since the rebaser rewrites its bytes"
                : null;
            if (problem is not null)
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.InvalidCapturePolicy,
                        field.Locations.FirstOrDefault() ?? Location.None,
                        field.Name,
                        Symbols.Name(type),
                        problem
                    )
                );
        }
    }

    static void CheckIndex(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        AttributeData attribute,
        Location location
    )
    {
        var fields = IndexModelBuilder.Fields(attribute, out var kind);
        var keyFrom = Symbols.Named<string?>(attribute, "KeyFrom", null);

        var keys = new List<ITypeSymbol>();
        if (keyFrom is not null)
        {
            if (
                type.GetMembers(keyFrom).OfType<IMethodSymbol>().FirstOrDefault(m => m.IsStatic) is
                { } method
            )
                keys.Add(IndexModelBuilder.KeyOf(method));
        }
        else
        {
            foreach (var name in fields)
            {
                if (
                    type.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault(f => !f.IsStatic) is
                    { } field
                )
                    keys.Add(field.Type);
            }
        }

        var ordered = kind == 2;
        foreach (var key in keys)
        {
            if (ordered ? IsComparable(key) : IsEquatable(key))
                continue;
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.IndexKeyNotComparable,
                    attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? location,
                    ordered ? "ordered" : "hashed",
                    Symbols.Name(type),
                    Symbols.Name(key),
                    ordered ? "IComparable<T>" : "IEquatable<T>"
                )
            );
        }
    }

    static bool IsEquatable(ITypeSymbol type) =>
        type.SpecialType != SpecialType.None
        || type.TypeKind == TypeKind.Enum
        || Implements(type, "IEquatable");

    static bool IsComparable(ITypeSymbol type) =>
        (type.SpecialType != SpecialType.None && type.SpecialType != SpecialType.System_Object)
        || type.TypeKind == TypeKind.Enum
        || Implements(type, "IComparable");

    static bool Implements(ITypeSymbol type, string name) =>
        type.AllInterfaces.Any(i =>
            i.Name == name
            && i.ContainingNamespace?.ToDisplayString() == "System"
            && i.TypeArguments.Length == 1
            && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], type)
        );
}
