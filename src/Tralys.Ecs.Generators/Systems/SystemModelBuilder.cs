using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Metadata;

namespace Tralys.Ecs.Generators.Systems;

sealed record SystemModel(
    string Namespace,
    EquatableArray<ContainerModel> Containers,
    string Keyword,
    string TypeName,
    string TypeHint,
    string DisplayName,
    bool Parallel,
    EquatableArray<string> Lookups,
    EquatableArray<Problem> Problems,
    string FullName,
    string? Set,
    bool Reachable
);

static class SystemModelBuilder
{
    public static SystemModel? Build(GeneratorAttributeSyntaxContext context)
    {
        if (
            context.TargetSymbol is not INamedTypeSymbol type
            || context.TargetNode is not ClassDeclarationSyntax
        )
            return null;
        var problems = new List<Problem>();
        var location = LocationInfo.From(type.Locations.FirstOrDefault());
        var (containers, notPartial) = Symbols.Containers(type);
        if (!Symbols.IsPartial(type) || notPartial is not null)
            problems.Add(
                new Problem(Diagnostics.NotPartial, location, Symbols.Name(notPartial ?? type))
            );
        if (!Symbols.DerivesFrom(type, Symbols.SystemBase))
            problems.Add(
                new Problem(Diagnostics.SystemNotSystemBase, location, Symbols.Name(type))
            );

        var lookups = new List<string>();
        var parallel = false;
        for (
            var current = type;
            current is not null && !Symbols.Is(current, Symbols.SystemBase);
            current = current.BaseType
        )
        {
            parallel |= RunsParallelHostQuery(current, context.SemanticModel);
            var own = SymbolEqualityComparer.Default.Equals(current, type);
            foreach (var member in current.GetMembers())
            {
                switch (member)
                {
                    case IFieldSymbol field when !field.IsStatic && IsLookup(field.Type):
                        if (!own && !Symbols.Has(current, Symbols.System))
                            problems.Add(
                                new Problem(
                                    Diagnostics.UnboundLookupField,
                                    location,
                                    field.Name,
                                    Symbols.Name(current)
                                )
                            );
                        if (!own)
                            break;
                        if (field.IsReadOnly)
                            problems.Add(
                                new Problem(
                                    Diagnostics.ReadonlyLookupField,
                                    LocationInfo.From(field.Locations.FirstOrDefault()),
                                    field.Name,
                                    Symbols.Name(type)
                                )
                            );
                        else
                            lookups.Add(field.Name);
                        break;
                    case IMethodSymbol method when QueryAttribute(method) is { } query:
                        parallel |= IsParallel(query);
                        break;
                }
            }
        }

        string? set = null;
        foreach (var attribute in type.GetAttributes())
        {
            if (
                !Symbols.IsAttribute(attribute, Symbols.InSet)
                || attribute.AttributeClass is not { TypeArguments.Length: 1 } inSet
            )
                continue;
            var target = inSet.TypeArguments[0];
            if (Symbols.Has(target, Symbols.SystemSet))
                set = Symbols.Full(target);
            else
                problems.Add(
                    new Problem(
                        Diagnostics.InvalidSet,
                        location,
                        Symbols.Name(type),
                        Symbols.Name(target)
                    )
                );
        }

        return new SystemModel(
            Symbols.Namespace(type),
            containers,
            type.IsRecord ? "record" : "class",
            Symbols.DeclarationName(type),
            ModuleEmitter.HintName(Symbols.Name(type)),
            Symbols.Name(type),
            parallel,
            new([.. lookups]),
            new([.. problems]),
            Symbols.Full(type),
            set,
            Reachable(type)
        );
    }

    static readonly string[] RunScopes = ["ForSubtree", "ChildrenOf", "TargetsOf", "For", ""];

    static (AttributeData Attribute, bool Collect)? QueryAttribute(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            if (Symbols.IsAttribute(attribute, Symbols.Query))
                return (attribute, false);
            if (Symbols.IsAttribute(attribute, Symbols.Collect))
                return (attribute, true);
        }
        return null;
    }

    static bool IsParallel((AttributeData Attribute, bool Collect) query) =>
        !query.Collect && Symbols.Named(query.Attribute, "Parallel", false);

    static bool RunsParallelHostQuery(INamedTypeSymbol type, SemanticModel model)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            var node = reference.GetSyntax();
            var semantic =
                node.SyntaxTree == model.SyntaxTree
                    ? model
                    : model.Compilation.GetSemanticModel(node.SyntaxTree);
            foreach (
                var invocation in node.DescendantNodes(n =>
                        n == node || n is not TypeDeclarationSyntax
                    )
                    .OfType<InvocationExpressionSyntax>()
            )
            {
                if (
                    semantic.GetSymbolInfo(invocation).Symbol is null
                    && HostRun(invocation, semantic, type) is { } query
                    && IsParallel(query)
                )
                    return true;
            }
        }
        return false;
    }

    // A generated run such as Host.RunMoveChildrenOf(world, e) is unbound here, so its query is found by name.
    static (AttributeData Attribute, bool Collect)? HostRun(
        InvocationExpressionSyntax invocation,
        SemanticModel semantic,
        INamedTypeSymbol self
    )
    {
        var (receiver, called) = invocation.Expression switch
        {
            SimpleNameSyntax simple => (self, simple),
            MemberAccessExpressionSyntax access => (
                semantic.GetSymbolInfo(access.Expression).Symbol as ITypeSymbol
                    ?? semantic.GetTypeInfo(access.Expression).Type,
                access.Name
            ),
            _ => (null, null),
        };
        if (receiver is not INamedTypeSymbol host || called is null)
            return null;
        var name = called.Identifier.ValueText;
        if (!name.StartsWith("Run", StringComparison.Ordinal))
            return null;
        for (INamedTypeSymbol? own = self; own is not null; own = own.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(own, host))
                return null;
        }
        foreach (var scope in RunScopes)
        {
            if (
                name.Length <= "Run".Length + scope.Length
                || !name.EndsWith(scope, StringComparison.Ordinal)
            )
                continue;
            var method = name.Substring("Run".Length, name.Length - "Run".Length - scope.Length);
            for (INamedTypeSymbol? type = host; type is not null; type = type.BaseType)
            {
                foreach (var candidate in type.GetMembers(method).OfType<IMethodSymbol>())
                {
                    if (QueryAttribute(candidate) is { } query)
                        return query;
                }
            }
        }
        return null;
    }

    static bool Reachable(INamedTypeSymbol type)
    {
        for (
            INamedTypeSymbol? current = type;
            current is not null;
            current = current.ContainingType
        )
        {
            if (
                current.DeclaredAccessibility
                is not (
                    Accessibility.Public
                    or Accessibility.Internal
                    or Accessibility.ProtectedOrInternal
                )
            )
                return false;
        }
        return type.TypeParameters.Length == 0 && !type.IsAbstract;
    }

    static bool IsLookup(ITypeSymbol type) =>
        Symbols.Is(type, Symbols.Lookup)
        || Symbols.Is(type, Symbols.IndexLookup)
        || Symbols.Is(type, Symbols.RefLookup)
        || Symbols.Is(type, Symbols.ViewLookup);
}
