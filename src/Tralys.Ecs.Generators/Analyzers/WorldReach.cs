using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Tralys.Ecs.Generators.Analyzers;

readonly record struct Move(int Storage, string Label);

sealed class WorldReach(Compilation compilation)
{
    static readonly string[] Handles =
    [
        Symbols.World,
        Symbols.WorldEntity,
        Symbols.ViewLookup,
        Symbols.RowsHandle,
        Symbols.ObserverContext,
    ];

    readonly Dictionary<ITypeSymbol, bool> holds = new(SymbolEqualityComparer.Default);
    readonly Dictionary<ITypeSymbol, bool> statics = new(SymbolEqualityComparer.Default);
    readonly ConcurrentDictionary<IAssemblySymbol, bool> sees = new(SymbolEqualityComparer.Default);
    readonly ConcurrentDictionary<IMethodSymbol, bool> captures = new(
        SymbolEqualityComparer.Default
    );
    readonly Lazy<Compilation> everything = new(() =>
        compilation.WithOptions(
            compilation.Options.WithMetadataImportOptions(MetadataImportOptions.All)
        )
    );

    public Move Of(IOperation operation)
    {
        switch (operation)
        {
            case IInvocationOperation
            {
                TargetMethod: { MethodKind: MethodKind.DelegateInvoke } invoke
            }:
                return new Move(Effects.Moves, EffectGraph.Name(invoke));
            case IInvocationOperation invocation:
                return Call(
                    invocation.TargetMethod,
                    invocation.Instance,
                    [.. invocation.Arguments.Select(a => a.Value)]
                );
            case IPropertyReferenceOperation property:
                var move = default(Move);
                foreach (var accessor in EffectGraph.Accessors(property))
                {
                    var call = Call(
                        accessor,
                        property.Instance,
                        [.. property.Arguments.Select(a => a.Value)]
                    );
                    if (call.Storage != 0)
                        move = call with { Storage = call.Storage | move.Storage };
                }
                return move;
            case IObjectCreationOperation { Constructor: { } constructor } creation:
                return Call(constructor, null, [.. creation.Arguments.Select(a => a.Value)]);
            case IUnaryOperation { OperatorMethod: { } unary } operand:
                return Call(unary, null, [operand.Operand]);
            case IBinaryOperation { OperatorMethod: { } binary } operands:
                return Call(binary, null, [operands.LeftOperand, operands.RightOperand]);
            case IConversionOperation { OperatorMethod: { } conversion } converted:
                return Call(conversion, null, [converted.Operand]);
            default:
                return default;
        }
    }

    // Only the runtime, whose build verifies them, and the generator write [Effect], so a declared one is trusted.
    Move Call(IMethodSymbol method, IOperation? instance, IOperation[] arguments)
    {
        var label = EffectGraph.Name(method);
        arguments = [.. arguments.Where(a => !Empty(a))];
        if (arguments.Any(a => a.Type is { TypeKind: TypeKind.Delegate }))
            return new Move(Effects.Moves, label);
        if (Effects.Declared(method) is { } declared)
            return new Move(declared & Effects.Moves, label);
        var reaches = Symbols.InRuntime(method)
            ? method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor
                && (Foreign(instance?.Type) || arguments.Any(a => Foreign(a.Type)))
                && Reaches(method, instance, arguments)
            : !Trivial(method, arguments)
                && (
                    Dispatched(method, instance)
                    || (Sees(method.ContainingAssembly) && Reaches(method, instance, arguments))
                );
        return reaches ? new Move(Effects.Moves, label) : default;
    }

    static bool Empty(IOperation argument)
    {
        while (argument is IConversionOperation conversion)
            argument = conversion.Operand;
        return argument is ILiteralOperation or IDefaultValueOperation;
    }

    bool Dispatched(IMethodSymbol method, IOperation? instance)
    {
        if (!(method.IsAbstract || method.IsVirtual || method.IsOverride) || method.IsSealed)
            return false;
        // An interface is implemented anywhere; a class virtual rooted outside code built on the runtime is taken to stay outside it.
        if (
            method.ContainingType.TypeKind != TypeKind.Interface
            && !Sees(Root(method).ContainingAssembly)
        )
            return false;
        if (method.IsStatic)
            return true;
        return instance?.Type
                is not (
                    INamedTypeSymbol { IsValueType: true }
                    or INamedTypeSymbol { IsSealed: true }
                )
            && instance is not IInstanceReferenceOperation { Syntax: BaseExpressionSyntax };
    }

    static IMethodSymbol Root(IMethodSymbol method)
    {
        var root = method.OriginalDefinition;
        while (root.OverriddenMethod is { } overridden)
            root = overridden.OriginalDefinition;
        return root;
    }

    bool Reaches(IMethodSymbol method, IOperation? instance, IOperation[] arguments)
    {
        if (
            !method.IsStatic
            && method.MethodKind != MethodKind.Constructor
            && (
                Holds(instance?.Type)
                || Holds(method.ContainingType)
                || (method.MethodKind == MethodKind.LocalFunction && Captures(method))
            )
        )
            return true;
        return HoldsStatic(method.ContainingType) || arguments.Any(a => Holds(a.Type));
    }

    // Consumer code the runtime is handed may call back into anything it holds.
    bool Foreign(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && !Symbols.InRuntime(named)
        && Sees(named.ContainingAssembly);

    // Code built without the runtime can reach a world only by calling back through a delegate it is handed.
    bool Sees(IAssemblySymbol? assembly) =>
        assembly is not null
        && sees.GetOrAdd(
            assembly,
            static a =>
                a.GetTypeByMetadataName(Effects.Attribute) is not null
                || a.Modules.Any(m =>
                    m.ReferencedAssemblySymbols.Any(r =>
                        r.GetTypeByMetadataName(Effects.Attribute) is not null
                    )
                )
        );

    // A property read is a read: a getter that changed structure would break every caller, not only a borrow.
    bool Trivial(IMethodSymbol method, IOperation[] arguments) =>
        (
            method.MethodKind == MethodKind.PropertyGet
            && !(
                method.AssociatedSymbol is IPropertySymbol { IsIndexer: true }
                && arguments.Any(a => Holds(a.Type))
            )
        )
        || (
            method.AssociatedSymbol
                is IPropertySymbol
                {
                    IsAbstract: false,
                    IsVirtual: false,
                    IsOverride: false,
                    IsExtern: false,
                    IsPartialDefinition: false,
                    PartialImplementationPart: null,
                }
            && method.ContainingType.TypeKind != TypeKind.Interface
            && (
                method.IsImplicitlyDeclared
                || (
                    method.DeclaringSyntaxReferences.Length == 1
                    && method.DeclaringSyntaxReferences[0].GetSyntax()
                        is AccessorDeclarationSyntax { Body: null, ExpressionBody: null }
                )
            )
        );

    bool Captures(IMethodSymbol local) =>
        captures.GetOrAdd(
            local,
            l =>
                l.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax() is LocalFunctionStatementSyntax syntax
                    && compilation.GetSemanticModel(syntax.SyntaxTree).AnalyzeDataFlow(syntax)
                        is { Succeeded: true } flow
                    && flow.CapturedInside.Any(captured =>
                        Holds(
                            captured switch
                            {
                                ILocalSymbol variable => variable.Type,
                                IParameterSymbol parameter => parameter.Type,
                                _ => null,
                            }
                        )
                    )
                )
        );

    public bool Holds(ITypeSymbol? type)
    {
        if (type is null)
            return false;
        lock (holds)
        {
            if (!holds.TryGetValue(type, out var found))
                holds[type] = found = Search(type, new(SymbolEqualityComparer.Default));
            return found;
        }
    }

    bool HoldsStatic(INamedTypeSymbol? type)
    {
        if (type is null || !Sees(type.ContainingAssembly))
            return false;
        lock (holds)
        {
            if (!statics.TryGetValue(type, out var found))
                statics[type] = found = Members(type, false)
                    .Any(held => Search(held, new(SymbolEqualityComparer.Default)));
            return found;
        }
    }

    // A type seen again is already being followed; only whole searches are remembered, since one cut off by a cycle reads false.
    bool Search(ITypeSymbol type, HashSet<ITypeSymbol> seen)
    {
        if (holds.TryGetValue(type, out var known))
            return known;
        if (!seen.Add(type))
            return false;
        return type switch
        {
            IArrayTypeSymbol array => Search(array.ElementType, seen),
            ITypeParameterSymbol parameter => parameter.ConstraintTypes.Any(c => Search(c, seen)),
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } => true,
            INamedTypeSymbol named => Handles.Any(handle => Symbols.Is(named, handle))
                || Symbols.Implements(named, Symbols.EntityView)
                || named.TypeArguments.Any(argument => Search(argument, seen))
                || (
                    Symbols.InRuntime(named)
                        ? Exposes(named, seen)
                        : Sees(named.ContainingAssembly)
                            && Members(named, true).Any(held => Search(held, seen))
                ),
            _ => false,
        };
    }

    // A runtime type is reached only through its public members, whose declared effects the build verifies.
    bool Exposes(INamedTypeSymbol type, HashSet<ITypeSymbol> seen)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public)
                    continue;
                var reaches = member switch
                {
                    IMethodSymbol method => (Effects.Declared(method) & Effects.Moves) is > 0,
                    IFieldSymbol field => Search(field.Type, seen),
                    IPropertySymbol property => Search(property.Type, seen),
                    _ => false,
                };
                if (reaches)
                    return true;
            }
        }
        return false;
    }

    // A referenced assembly is imported with its public and protected members only, so a type's private fields are read from a full import.
    INamedTypeSymbol Complete(INamedTypeSymbol type)
    {
        if (
            !type.DeclaringSyntaxReferences.IsEmpty
            || type.ContainingAssembly is not { } assembly
            || compilation.GetMetadataReference(assembly) is not { } reference
        )
            return type;
        return
            everything.Value.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol full
            && full.GetTypeByMetadataName(MetadataName(type.OriginalDefinition)) is { } found
            ? found
            : type;
    }

    static string MetadataName(INamedTypeSymbol type) =>
        type.ContainingType is { } outer ? MetadataName(outer) + "+" + type.MetadataName
        : type.ContainingNamespace is { IsGlobalNamespace: false } space
            ? space.ToDisplayString() + "." + type.MetadataName
        : type.MetadataName;

    IEnumerable<ITypeSymbol> Members(INamedTypeSymbol type, bool instance)
    {
        for (
            INamedTypeSymbol? current = Complete(type);
            current is not null;
            current = current.BaseType
        )
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic == instance)
                    continue;
                switch (member)
                {
                    case IFieldSymbol field:
                        yield return field.Type;
                        break;
                    case IPropertySymbol property:
                        yield return property.Type;
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor
                        when instance:
                        foreach (var parameter in constructor.Parameters)
                            yield return parameter.Type;
                        break;
                }
            }
        }
    }
}
