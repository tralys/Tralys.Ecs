using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Tralys.Ecs.Generators;

readonly record struct Site(
    IMethodSymbol? Method,
    bool Dispatch,
    int Flags,
    string Label,
    ITypeSymbol? Receiver = null,
    Seam? Missing = null
);

sealed record Seam(string Name, Location Location);

sealed class EffectGraph
{
    public readonly Compilation Compilation;
    readonly ConcurrentDictionary<IMethodSymbol, Node> nodes = new(SymbolEqualityComparer.Default);
    readonly ConcurrentDictionary<IMethodSymbol, int> closures = new(
        SymbolEqualityComparer.Default
    );
    readonly Lazy<
        Dictionary<IMethodSymbol, List<(IMethodSymbol Method, INamedTypeSymbol For)>>
    > implementations;
    readonly Lazy<List<(ITypeSymbol Type, IMethodSymbol Target, bool Dispatch)>> creations;

    public EffectGraph(Compilation compilation)
    {
        Compilation = compilation;
        implementations = new(BuildImplementations);
        creations = new(BuildCreations);
    }

    public sealed class Node
    {
        public int Direct;
        public readonly List<(int Flags, string Why)> Roots = [];
        public readonly List<(IMethodSymbol Callee, string Label)> Edges = [];
        public readonly List<Seam> Seams = [];

        public void Root(int flags, string why)
        {
            Direct |= flags;
            Roots.Add((flags, why));
        }
    }

    public static IMethodSymbol Definition(IMethodSymbol method) =>
        (method.ReducedFrom ?? method).OriginalDefinition;

    public Node NodeOf(IMethodSymbol method) => nodes.GetOrAdd(Definition(method), Build);

    public bool InSource(ISymbol symbol) =>
        SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, Compilation.Assembly);

    public static bool Visible(ISymbol symbol)
    {
        for (
            var current = symbol;
            current is not null and not INamespaceSymbol;
            current = current.ContainingSymbol
        )
        {
            if (
                current.DeclaredAccessibility
                is not (
                    Accessibility.Public
                    or Accessibility.Protected
                    or Accessibility.ProtectedOrInternal
                )
            )
                return false;
        }
        return true;
    }

    public int Closure(IMethodSymbol method)
    {
        var start = Definition(method);
        if (closures.TryGetValue(start, out var known))
            return known;
        var flags = 0;
        foreach (var reached in Reach(start))
            flags |= NodeOf(reached).Direct;
        closures[start] = flags;
        return flags;
    }

    public IEnumerable<IMethodSymbol> Reach(IMethodSymbol start)
    {
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { start };
        var queue = new Queue<IMethodSymbol>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            yield return method;
            foreach (var (callee, _) in NodeOf(method).Edges)
            {
                if (seen.Add(callee))
                    queue.Enqueue(callee);
            }
        }
    }

    public string Explain(IMethodSymbol start, int flags) =>
        string.Join(
            "; ",
            Effects
                .Flags(flags)
                .Select(flag =>
                    Effects.Describe(flag) + " through " + Path(Definition(start), flag)
                )
        );

    public string Path(IMethodSymbol start, Func<Node, string?> stop)
    {
        var parents = new Dictionary<IMethodSymbol, IMethodSymbol?>(SymbolEqualityComparer.Default)
        {
            [start] = null,
        };
        var queue = new Queue<IMethodSymbol>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            var node = NodeOf(method);
            if (stop(node) is { } why)
            {
                var chain = new List<string>();
                for (IMethodSymbol? at = method; at is not null; at = parents[at])
                    chain.Add(Name(at));
                chain.Reverse();
                return string.Join(" → ", chain) + " (" + why + ")";
            }
            foreach (var (callee, _) in node.Edges)
            {
                if (parents.ContainsKey(callee))
                    continue;
                parents[callee] = method;
                queue.Enqueue(callee);
            }
        }
        return Name(start);
    }

    string Path(IMethodSymbol start, int flag) =>
        Path(start, node => node.Roots.FirstOrDefault(r => (r.Flags & flag) != 0).Why);

    public IReadOnlyList<IMethodSymbol> Implementations(IMethodSymbol method) =>
        implementations.Value.TryGetValue(Definition(method), out var found)
            ?
            [
                .. found
                    .Select(f => f.Method)
                    .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default),
            ]
            : [];

    // An implementation counts for the type it was found for, which may inherit it from a base that never names the contract.
    IEnumerable<IMethodSymbol> Implementations(IMethodSymbol method, ITypeSymbol? receiver) =>
        implementations.Value.TryGetValue(Definition(method), out var found)
            ? found.Where(f => Converts(f.For, receiver)).Select(f => f.Method)
            : [];

    static bool Converts(INamedTypeSymbol type, ITypeSymbol? receiver)
    {
        if (receiver is not INamedTypeSymbol target)
            return true;
        var definition = target.OriginalDefinition;
        if (target.TypeKind == TypeKind.Interface)
            return type.AllInterfaces.Any(i =>
                SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, definition)
            );
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, definition))
                return true;
        }
        return false;
    }

    public bool Overridable(IMethodSymbol method) =>
        InSource(method)
        && Visible(method)
        && (method.IsAbstract || method.IsVirtual || (method.IsOverride && !method.IsSealed))
        && method.ContainingType is { } type
        && (type.TypeKind == TypeKind.Interface || !type.IsSealed);

    static bool Dispatches(IMethodSymbol method) =>
        method.IsAbstract
        || method.IsVirtual
        || method.IsOverride
        || method.ContainingType?.TypeKind == TypeKind.Interface;

    Node Build(IMethodSymbol method)
    {
        var node = new Node();
        var declared = Effects.Declared(method);
        if (Bodies(method) is not { } bodies)
        {
            if (declared is { } stated)
                node.Root(stated, "declares [Effect(" + Effects.Describe(stated) + ")]");
            return node;
        }
        if (declared is { } primitive && !Visible(method))
            node.Root(primitive, "is marked [Effect(" + Effects.Describe(primitive) + ")]");
        foreach (var body in bodies)
            Walk(body, node, root: true);
        if (ImplicitBase(method, bodies) is { } baseConstructor)
            node.Edges.Add((baseConstructor, Name(baseConstructor)));
        return node;
    }

    // A constructor with no body of its own, or a primary one with no base arguments, still runs its base's parameterless constructor.
    IMethodSymbol? ImplicitBase(IMethodSymbol method, List<IOperation> bodies) =>
        method is { MethodKind: MethodKind.Constructor, IsStatic: false }
        && method.ContainingType is { TypeKind: TypeKind.Class, BaseType: { } baseType }
        && !bodies.Any(body =>
            body is IConstructorBodyOperation { Initializer: not null }
            || body.Syntax is PrimaryConstructorBaseTypeSyntax
        )
        && baseType.InstanceConstructors.FirstOrDefault(c => c.Parameters.IsEmpty)
            is { } constructor
        && InSource(constructor)
            ? constructor
            : null;

    void Walk(IOperation operation, Node node, bool root)
    {
        switch (operation)
        {
            case IAnonymousFunctionOperation lambda when !root:
                node.Edges.Add((lambda.Symbol, Name(lambda.Symbol)));
                return;
            case ILocalFunctionOperation local when !root:
                node.Edges.Add((local.Symbol, Name(local.Symbol)));
                return;
            case IDelegateCreationOperation { Target: IMethodReferenceOperation reference }:
                Add(node, Call(reference.Method, reference.IsVirtual, reference.Instance?.Type));
                break;
        }
        foreach (var site in Sites(operation))
            Add(node, site);
        foreach (var child in operation.ChildOperations)
            Walk(child, node, root: false);
    }

    void Add(Node node, in Site site)
    {
        if (site.Missing is { } missing)
            node.Seams.Add(missing);
        if (site.Flags != 0)
            node.Root(site.Flags, site.Label);
        if (site.Method is not { } method)
            return;
        node.Edges.Add((method, site.Label));
        if (!site.Dispatch)
            return;
        foreach (var implementation in Implementations(method, site.Receiver))
            node.Edges.Add((implementation, Name(implementation)));
        if (!Overridable(method))
            return;
        if (Effects.Declared(method) is { } seam)
            node.Root(seam, "may run a consumer's " + Name(method));
        else
            node.Seams.Add(
                new Seam(Name(method), method.Locations.FirstOrDefault() ?? Location.None)
            );
    }

    public IEnumerable<Site> Sites(IOperation operation)
    {
        switch (operation)
        {
            case IInvocationOperation { TargetMethod: var target } invocation:
                if (target.MethodKind == MethodKind.DelegateInvoke)
                {
                    if (
                        invocation.Instance is not { } instance
                        || Unwrap(instance) is not IParameterReferenceOperation
                    )
                        foreach (var created in Created(target.ContainingType))
                            yield return created;
                    break;
                }
                yield return Call(
                    target,
                    invocation.IsVirtual || target.IsAbstract,
                    invocation.Instance?.Type
                );
                foreach (var argument in invocation.Arguments)
                {
                    if (Forwarded(argument.Value) is { } stored)
                        foreach (var created in Created(stored))
                            yield return created;
                }
                break;
            case IPropertyReferenceOperation property:
                foreach (var accessor in Accessors(property))
                    yield return Call(accessor, true, property.Instance?.Type);
                break;
            case IObjectCreationOperation { Constructor: { } constructor }:
                yield return Call(constructor, false);
                break;
            case IFieldReferenceOperation field
                when field.Field.Type is not IFunctionPointerTypeSymbol
                    && Effects.Declared(field.Field) is { } written
                    && IsWritten(field):
                yield return new Site(null, false, written, "writes " + Name(field.Field));
                break;
            case IFunctionPointerInvocationOperation pointer:
                if (
                    Unwrap(pointer.Target) is IFieldReferenceOperation { Field: var through }
                    && Effects.Declared(through) is { } declared
                )
                    yield return new Site(null, false, declared, "calls through " + Name(through));
                else
                    yield return new Site(
                        null,
                        false,
                        0,
                        "a function pointer",
                        Missing: new Seam(
                            pointer.Target.Syntax.ToString(),
                            pointer.Syntax.GetLocation()
                        )
                    );
                break;
            case IConversionOperation { OperatorMethod: { } conversion }:
                yield return Call(conversion, false);
                break;
            case IUnaryOperation { OperatorMethod: { } unary }:
                yield return Call(unary, false);
                break;
            case IBinaryOperation { OperatorMethod: { } binary }:
                yield return Call(binary, false);
                break;
            case IIncrementOrDecrementOperation { OperatorMethod: { } step }:
                yield return Call(step, false);
                break;
            case ICompoundAssignmentOperation { OperatorMethod: { } compound }:
                yield return Call(compound, false);
                break;
            case IForEachLoopOperation { Syntax: CommonForEachStatementSyntax syntax } loop
                when loop.SemanticModel is { } model:
                var info = model.GetForEachStatementInfo(syntax);
                if (info.GetEnumeratorMethod is { } start)
                    yield return Call(start, true, Unwrap(loop.Collection).Type);
                var enumerator = info.GetEnumeratorMethod?.ReturnType;
                foreach (
                    var step in new[]
                    {
                        info.MoveNextMethod,
                        info.CurrentProperty?.GetMethod,
                        info.DisposeMethod,
                    }
                )
                {
                    if (step is not null)
                        yield return Call(step, true, enumerator);
                }
                break;
            case IUsingOperation { Resources: var resources }:
                foreach (var dispose in Disposes(resources))
                    yield return dispose;
                break;
            case IUsingDeclarationOperation { DeclarationGroup: var group }:
                foreach (var dispose in Disposes(group))
                    yield return dispose;
                break;
        }
    }

    static Site Call(IMethodSymbol method, bool dispatch, ITypeSymbol? receiver = null) =>
        new(Definition(method), dispatch && Dispatches(method), 0, Name(method), receiver);

    IEnumerable<Site> Disposes(IOperation resources)
    {
        var types = resources is IVariableDeclarationGroupOperation group
            ? group
                .Declarations.SelectMany(d => d.Declarators)
                .Select(d => (ITypeSymbol?)d.Symbol.Type)
            : [resources.Type];
        var disposable = Compilation
            .GetSpecialType(SpecialType.System_IDisposable)
            .GetMembers("Dispose")
            .OfType<IMethodSymbol>()
            .FirstOrDefault();
        foreach (var type in types)
        {
            if (type is null)
                continue;
            if (type.IsRefLikeType)
            {
                if (
                    type.GetMembers("Dispose")
                        .OfType<IMethodSymbol>()
                        .FirstOrDefault(m =>
                            !m.IsStatic && m.Parameters.All(p => p.IsOptional || p.IsParams)
                        ) is
                    { } pattern
                )
                    yield return Call(pattern, false);
                continue;
            }
            if (disposable is null)
                continue;
            if (
                type is INamedTypeSymbol { TypeKind: not TypeKind.Interface } named
                && named.FindImplementationForInterfaceMember(disposable) is IMethodSymbol own
            )
                yield return Call(own, !named.IsSealed && !named.IsValueType, type);
            else
                yield return Call(disposable, true, type);
        }
    }

    public static IEnumerable<IMethodSymbol> Accessors(IPropertyReferenceOperation reference)
    {
        var property = reference.Property;
        if (property.ReturnsByRef || property.ReturnsByRefReadonly)
        {
            if (property.GetMethod is { } byRef)
                yield return byRef;
            yield break;
        }
        var parent = reference.Parent;
        var assigned =
            parent is ISimpleAssignmentOperation simple && simple.Target == reference
            || DeconstructedInto(reference);
        var both =
            parent is ICompoundAssignmentOperation compound && compound.Target == reference
            || parent is IIncrementOrDecrementOperation step && step.Target == reference
            || parent is ICoalesceAssignmentOperation coalesce && coalesce.Target == reference;
        if ((!assigned || both) && property.GetMethod is { } get)
            yield return get;
        if ((assigned || both) && property.SetMethod is { } set)
            yield return set;
    }

    static bool DeconstructedInto(IOperation reference)
    {
        var target = reference;
        while (target.Parent is ITupleOperation tuple)
            target = tuple;
        return target.Parent is IDeconstructionAssignmentOperation deconstruction
            && deconstruction.Target == target;
    }

    static bool IsWritten(IOperation reference) =>
        DeconstructedInto(reference)
        || reference.Parent switch
        {
            ISimpleAssignmentOperation assignment => assignment.Target == reference,
            ICompoundAssignmentOperation compound => compound.Target == reference,
            IIncrementOrDecrementOperation step => step.Target == reference,
            ICoalesceAssignmentOperation coalesce => coalesce.Target == reference,
            IArgumentOperation argument => argument.Parameter?.RefKind
                is RefKind.Ref
                    or RefKind.Out,
            _ => false,
        };

    static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation or IParenthesizedOperation)
            operation = operation switch
            {
                IConversionOperation conversion => conversion.Operand,
                IParenthesizedOperation parenthesized => parenthesized.Operand,
                _ => operation,
            };
        return operation;
    }

    // A delegate made at the call is charged where it is made, and a parameter where its caller makes it.
    static ITypeSymbol? Forwarded(IOperation value) =>
        value.Type is { TypeKind: TypeKind.Delegate } type
        && Unwrap(value)
            is not (
                IDelegateCreationOperation
                or IAnonymousFunctionOperation
                or IParameterReferenceOperation
                or ILiteralOperation
                or IDefaultValueOperation
            )
            ? type
            : null;

    IEnumerable<Site> Created(ITypeSymbol delegateType)
    {
        foreach (var (type, target, dispatch) in creations.Value)
        {
            if (Unifies(type, delegateType))
                yield return new Site(target, dispatch, 0, Name(target));
        }
    }

    static bool Unifies(ITypeSymbol a, ITypeSymbol b)
    {
        if (a is ITypeParameterSymbol || b is ITypeParameterSymbol)
            return true;
        if (a is IArrayTypeSymbol arrayA && b is IArrayTypeSymbol arrayB)
            return Unifies(arrayA.ElementType, arrayB.ElementType);
        if (
            a is IFunctionPointerTypeSymbol { Signature: var left }
            && b is IFunctionPointerTypeSymbol { Signature: var right }
        )
            return left.Parameters.Length == right.Parameters.Length
                && left.RefKind == right.RefKind
                && Unifies(left.ReturnType, right.ReturnType)
                && left.Parameters.Zip(
                        right.Parameters,
                        (l, r) => l.RefKind == r.RefKind && Unifies(l.Type, r.Type)
                    )
                    .All(same => same);
        if (a is INamedTypeSymbol namedA && b is INamedTypeSymbol namedB)
        {
            if (
                !SymbolEqualityComparer.Default.Equals(
                    namedA.OriginalDefinition,
                    namedB.OriginalDefinition
                )
            )
                return false;
            for (var i = 0; i < namedA.TypeArguments.Length; i++)
            {
                if (!Unifies(namedA.TypeArguments[i], namedB.TypeArguments[i]))
                    return false;
            }
            return true;
        }
        return SymbolEqualityComparer.Default.Equals(a, b);
    }

    List<IOperation>? Bodies(IMethodSymbol method)
    {
        method = method.PartialImplementationPart ?? method;
        if (!InSource(method) || method.IsAbstract || method.IsExtern)
            return null;
        if (method.MethodKind == MethodKind.AnonymousFunction)
            return Lambda(method);
        var bodies = new List<IOperation>();
        var declared = false;
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax();
            var model = Compilation.GetSemanticModel(syntax.SyntaxTree);
            switch (syntax)
            {
                case BaseMethodDeclarationSyntax { Body: null, ExpressionBody: null }:
                    continue;
                case AccessorDeclarationSyntax { Body: null, ExpressionBody: null }:
                    declared = true;
                    continue;
                case TypeDeclarationSyntax type:
                    declared = true;
                    if (
                        method.MethodKind == MethodKind.Constructor
                        && type
                            .BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>()
                            .FirstOrDefault()
                            is { } baseCall
                        && model.GetOperation(baseCall) is { } call
                    )
                        bodies.Add(call);
                    continue;
                case ArrowExpressionClauseSyntax arrow:
                    declared = true;
                    if (
                        (model.GetOperation(arrow) ?? model.GetOperation(arrow.Expression)) is
                        { } value
                    )
                        bodies.Add(value);
                    continue;
            }
            if (model.GetOperation(syntax) is not { } body)
                continue;
            declared = true;
            bodies.Add(body);
        }
        if (method.MethodKind == MethodKind.Constructor && !method.IsStatic)
        {
            declared = true;
            if (!bodies.Any(ChainsToThis))
                bodies.AddRange(Initializers(method.ContainingType));
        }
        return declared ? bodies : null;
    }

    List<IOperation>? Lambda(IMethodSymbol method)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax();
            var operation = Compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax);
            if (operation is IDelegateCreationOperation { Target: var target })
                operation = target;
            if (operation is IAnonymousFunctionOperation lambda)
                return [lambda];
        }
        return [];
    }

    static bool ChainsToThis(IOperation body) =>
        body.Syntax is ConstructorDeclarationSyntax { Initializer: { } initializer }
        && initializer.IsKind(SyntaxKind.ThisConstructorInitializer);

    IEnumerable<IOperation> Initializers(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic)
                continue;
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                EqualsValueClauseSyntax? initializer = reference.GetSyntax() switch
                {
                    VariableDeclaratorSyntax declarator => declarator.Initializer,
                    PropertyDeclarationSyntax property => property.Initializer,
                    _ => null,
                };
                if (
                    initializer is not null
                    && Compilation
                        .GetSemanticModel(initializer.SyntaxTree)
                        .GetOperation(initializer)
                        is { } operation
                )
                    yield return operation;
            }
        }
    }

    Dictionary<
        IMethodSymbol,
        List<(IMethodSymbol Method, INamedTypeSymbol For)>
    > BuildImplementations()
    {
        var map = new Dictionary<IMethodSymbol, List<(IMethodSymbol, INamedTypeSymbol)>>(
            SymbolEqualityComparer.Default
        );
        void Link(IMethodSymbol member, IMethodSymbol implementation, INamedTypeSymbol type)
        {
            var key = Definition(member);
            if (!map.TryGetValue(key, out var list))
                map[key] = list = [];
            var value = Definition(implementation);
            if (
                !list.Any(known =>
                    SymbolEqualityComparer.Default.Equals(known.Item1, value)
                    && SymbolEqualityComparer.Default.Equals(known.Item2, type)
                )
            )
                list.Add((value, type));
        }
        foreach (var type in Types(Compilation.Assembly.GlobalNamespace))
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                for (
                    var overridden = method.OverriddenMethod;
                    overridden is not null;
                    overridden = overridden.OverriddenMethod
                )
                    Link(overridden, method, type);
                foreach (var explicitMember in method.ExplicitInterfaceImplementations)
                    Link(explicitMember, method, type);
            }
            foreach (var contract in type.AllInterfaces)
            {
                foreach (var member in contract.GetMembers().OfType<IMethodSymbol>())
                {
                    if (
                        type.FindImplementationForInterfaceMember(member)
                            is IMethodSymbol implementation
                        && InSource(implementation)
                    )
                        Link(member, implementation, type);
                }
            }
        }
        return map;
    }

    static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol space)
            {
                foreach (var type in Types(space))
                    yield return type;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in Types(type))
                    yield return nested;
            }
        }
    }

    List<(ITypeSymbol Type, IMethodSymbol Target, bool Dispatch)> BuildCreations()
    {
        var found = new List<(ITypeSymbol, IMethodSymbol, bool)>();
        foreach (var tree in Compilation.SyntaxTrees)
        {
            var model = Compilation.GetSemanticModel(tree);
            foreach (var syntax in tree.GetRoot().DescendantNodesAndSelf().Where(OwnsBody))
            {
                if (model.GetOperation(syntax) is not { } body)
                    continue;
                foreach (var operation in body.DescendantsAndSelf())
                {
                    switch (operation)
                    {
                        case IDelegateCreationOperation
                        {
                            Type: { } type,
                            Target: IAnonymousFunctionOperation lambda,
                        }:
                            found.Add((type, lambda.Symbol, false));
                            break;
                        case IDelegateCreationOperation
                        {
                            Type: { } type,
                            Target: IMethodReferenceOperation reference,
                        }:
                            found.Add(
                                (
                                    type,
                                    Definition(reference.Method),
                                    reference.IsVirtual && Dispatches(reference.Method)
                                )
                            );
                            break;
                        case IMethodReferenceOperation reference
                            when (reference.Type ?? reference.Parent?.Type)
                                is IFunctionPointerTypeSymbol pointer:
                            found.Add((pointer, Definition(reference.Method), false));
                            break;
                    }
                }
            }
        }
        return found;
    }

    public IEnumerable<IMethodSymbol> PointedAt(IFunctionPointerTypeSymbol pointer) =>
        creations
            .Value.Where(c => c.Type is IFunctionPointerTypeSymbol && Unifies(c.Type, pointer))
            .Select(c => c.Target);

    static bool OwnsBody(SyntaxNode syntax) =>
        syntax switch
        {
            BaseMethodDeclarationSyntax
            or AccessorDeclarationSyntax
            or PrimaryConstructorBaseTypeSyntax => true,
            ArrowExpressionClauseSyntax { Parent: BasePropertyDeclarationSyntax } => true,
            EqualsValueClauseSyntax
            {
                Parent: VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax }
                    or PropertyDeclarationSyntax,
            } => true,
            GlobalStatementSyntax => true,
            _ => false,
        };

    public static string Name(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } lambda:
                return "a lambda in " + Name(lambda.ContainingSymbol);
            case IMethodSymbol { MethodKind: MethodKind.LocalFunction } local:
                return Name(local.ContainingSymbol) + "." + local.Name;
            case IMethodSymbol { AssociatedSymbol: IPropertySymbol property } accessor:
                return Owner(property)
                    + (property.IsIndexer ? "[]" : property.Name)
                    + (accessor.MethodKind == MethodKind.PropertySet ? " (set)" : "");
            case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor:
                return "new " + constructor.ContainingType.Name;
            default:
                return Owner(symbol) + symbol.Name;
        }
    }

    static string Owner(ISymbol symbol) =>
        symbol.ContainingType is { } type ? Name(type) + "." : "";

    static string Name(INamedTypeSymbol type) =>
        type.ContainingType is { } outer ? Name(outer) + "." + type.Name : type.Name;
}
