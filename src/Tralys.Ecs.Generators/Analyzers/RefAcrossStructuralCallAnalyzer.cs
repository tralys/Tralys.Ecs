using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Tralys.Ecs.Generators.Analyzers;

/// <summary>A borrow points into world storage: a <c>ref</c> to a component or row, or a local of a <c>[Borrow]</c> type. A later call that
/// can move that storage may leave it reading another entity or row. The rule is local: a member whose <c>[Effect]</c> declares a move of
/// the borrow's storage, a call into code outside the runtime that can reach a world handle through its receiver, its arguments, its
/// captures or its type's fields, and a delegate all count. Any two world handles may name the same world. Uses are checked along every
/// control-flow path, loop back-edges, exception handlers and finally blocks included. Query and collect bodies and chunk kernels are
/// exempt: nothing moves while a walk is open.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RefAcrossStructuralCallAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.RefAcrossStructuralCall];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (Effects.IsRuntime(start.Compilation))
                return;
            var reach = new WorldReach(start.Compilation);
            start.RegisterOperationBlockAction(block => Analyze(block, reach));
        });
    }

    readonly record struct Borrow(int MovedBy, string? StaleBy);

    // A borrow is held by a local, or by a flow capture the graph makes for a conditional ref.
    sealed class Holders : IEqualityComparer<object>
    {
        public static readonly Holders Instance = new();

        bool IEqualityComparer<object>.Equals(object? x, object? y) =>
            x is ISymbol a && y is ISymbol b
                ? SymbolEqualityComparer.Default.Equals(a, b)
                : object.Equals(x, y);

        int IEqualityComparer<object>.GetHashCode(object obj) =>
            obj is ISymbol symbol
                ? SymbolEqualityComparer.Default.GetHashCode(symbol)
                : obj.GetHashCode();
    }

    sealed class Findings
    {
        public readonly Dictionary<ILocalSymbol, (IOperation Use, string By)> First = new(
            SymbolEqualityComparer.Default
        );

        public void Report(ILocalSymbol local, IOperation use, string by)
        {
            if (
                !First.TryGetValue(local, out var known)
                || use.Syntax.SpanStart < known.Use.Syntax.SpanStart
            )
                First[local] = (use, by);
        }
    }

    static void Analyze(OperationBlockAnalysisContext context, WorldReach reach)
    {
        if (
            context.OwningSymbol is IMethodSymbol owner
            && (
                Symbols.Has(owner, Symbols.Query)
                || Symbols.Has(owner, Symbols.Collect)
                || Kernel(owner)
            )
        )
            return;
        var findings = new Findings();
        var roots = new HashSet<IOperation>();
        foreach (var block in context.OperationBlocks)
        {
            var root = block;
            while (root.Parent is not null)
                root = root.Parent;
            if (
                root.Kind
                    is OperationKind.MethodBody
                        or OperationKind.ConstructorBody
                        or OperationKind.Block
                && roots.Add(root)
            )
                Walk(context.GetControlFlowGraph(block), findings, reach);
        }
        foreach (var finding in findings.First)
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.RefAcrossStructuralCall,
                    finding.Value.Use.Syntax.GetLocation(),
                    finding.Key.Name,
                    finding.Value.By
                )
            );
    }

    static bool Kernel(IMethodSymbol method) =>
        method.ContainingType.AllInterfaces.Any(contract =>
            (
                Symbols.Is(contract, Symbols.ChunkKernel)
                || Symbols.Is(contract, Symbols.ReducingKernel)
            )
            && contract
                .GetMembers("Execute")
                .Any(member =>
                    SymbolEqualityComparer.Default.Equals(
                        method.ContainingType.FindImplementationForInterfaceMember(member),
                        method
                    )
                )
        );

    static void Walk(ControlFlowGraph graph, Findings findings, WorldReach reach)
    {
        var blocks = graph.Blocks;
        var entries = new Dictionary<object, Borrow>?[blocks.Length];
        entries[0] = new(Holders.Instance);
        var work = new Queue<int>();
        var queued = new bool[blocks.Length];
        work.Enqueue(0);
        queued[0] = true;
        while (work.Count > 0)
        {
            var index = work.Dequeue();
            queued[index] = false;
            if (entries[index] is not { } start)
                continue;
            var state = new Dictionary<object, Borrow>(start, Holders.Instance);
            Transfer(blocks[index], state, null, reach);
            foreach (var (next, handler, through) in Successors(blocks[index]))
            {
                var changed = Merge(
                    ref entries[next],
                    through.IsDefaultOrEmpty ? state : AfterFinally(blocks, through, state, reach)
                );
                if (handler)
                    changed |= Merge(ref entries[next], start);
                if (changed && !queued[next])
                {
                    queued[next] = true;
                    work.Enqueue(next);
                }
            }
        }
        foreach (var block in blocks)
        {
            if (entries[block.Ordinal] is { } start)
                Transfer(
                    block,
                    new Dictionary<object, Borrow>(start, Holders.Instance),
                    findings,
                    reach
                );
        }
        foreach (var function in graph.LocalFunctions)
            Walk(graph.GetLocalFunctionControlFlowGraph(function), findings, reach);
        foreach (var block in blocks)
        {
            var operations = block.BranchValue is { } value
                ? block.Operations.Add(value)
                : block.Operations;
            foreach (var operation in operations)
            {
                foreach (
                    var lambda in operation
                        .DescendantsAndSelf()
                        .OfType<IFlowAnonymousFunctionOperation>()
                )
                    Walk(graph.GetAnonymousFunctionControlFlowGraph(lambda), findings, reach);
            }
        }
    }

    // A branch out of a try runs its finally blocks first; the end of a finally names no destination, so the branch carries their effect.
    static IEnumerable<(
        int Ordinal,
        bool Handler,
        ImmutableArray<ControlFlowRegion> Through
    )> Successors(BasicBlock block)
    {
        foreach (var branch in new[] { block.FallThroughSuccessor, block.ConditionalSuccessor })
        {
            if (branch is null)
                continue;
            if (branch.Destination is { } destination)
                yield return (destination.Ordinal, false, branch.FinallyRegions);
            foreach (var region in branch.FinallyRegions)
                yield return (region.FirstBlockOrdinal, false, default);
        }
        // A nested graph's root links to the region of the outer graph it sits in, whose block ordinals are not this graph's.
        for (
            var region = block.EnclosingRegion;
            region is { Kind: not ControlFlowRegionKind.Root };
            region = region.EnclosingRegion
        )
        {
            if (
                region.Kind != ControlFlowRegionKind.Try
                || region.EnclosingRegion is not { } guarded
            )
                continue;
            foreach (var handler in guarded.NestedRegions)
            {
                if (handler != region)
                    yield return (handler.FirstBlockOrdinal, true, default);
            }
        }
    }

    static Dictionary<object, Borrow> AfterFinally(
        ImmutableArray<BasicBlock> blocks,
        ImmutableArray<ControlFlowRegion> regions,
        Dictionary<object, Borrow> state,
        WorldReach reach
    )
    {
        var after = new Dictionary<object, Borrow>(state, Holders.Instance);
        foreach (var region in regions)
        {
            for (
                var ordinal = region.FirstBlockOrdinal;
                ordinal <= region.LastBlockOrdinal;
                ordinal++
            )
                Transfer(blocks[ordinal], after, null, reach);
        }
        return after;
    }

    static bool Merge(ref Dictionary<object, Borrow>? into, Dictionary<object, Borrow> from)
    {
        if (into is null)
        {
            into = new(from, Holders.Instance);
            return true;
        }
        var changed = false;
        foreach (var pair in from)
        {
            if (!into.TryGetValue(pair.Key, out var known))
            {
                into[pair.Key] = pair.Value;
                changed = true;
                continue;
            }
            var movedBy = known.MovedBy | pair.Value.MovedBy;
            var staleBy = known.StaleBy ?? pair.Value.StaleBy;
            if (movedBy != known.MovedBy || staleBy != known.StaleBy)
            {
                into[pair.Key] = new Borrow(movedBy, staleBy);
                changed = true;
            }
        }
        return changed;
    }

    static void Transfer(
        BasicBlock block,
        Dictionary<object, Borrow> state,
        Findings? findings,
        WorldReach reach
    )
    {
        foreach (var operation in block.Operations)
            Visit(operation, state, findings, reach);
        if (block.BranchValue is { } value)
            Visit(value, state, findings, reach);
    }

    // Children run before their parent, so an argument read before a structural call is not a use after it.
    static void Visit(
        IOperation operation,
        Dictionary<object, Borrow> state,
        Findings? findings,
        WorldReach reach
    )
    {
        switch (operation)
        {
            case IFlowAnonymousFunctionOperation:
                return;
            case ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment
                when HoldsBorrow(target.Local)
                    && (assignment.IsRef || target.Local.RefKind == RefKind.None):
                Visit(assignment.Value, state, findings, reach);
                Bind(target.Local, assignment.Value, state);
                return;
            case IFlowCaptureOperation capture:
                Visit(capture.Value, state, findings, reach);
                Bind(capture.Id, capture.Value, state);
                return;
            case IIsPatternOperation { Pattern: var pattern } match
                when Declared(pattern) is { } local && HoldsBorrow(local):
                Visit(match.Value, state, findings, reach);
                Visit(pattern, state, findings, reach);
                Bind(local, match.Value, state);
                return;
            case ILocalReferenceOperation reference:
                if (state.TryGetValue(reference.Local, out var held) && held.StaleBy is { } by)
                    findings?.Report(reference.Local, reference, by);
                return;
        }
        foreach (var child in operation.ChildOperations)
            Visit(child, state, findings, reach);
        if (state.Count == 0)
            return;
        var move = reach.Of(operation);
        if (move.Storage != 0)
            Invalidate(state, move.Storage, move.Label);
    }

    static void Bind(object holder, IOperation value, Dictionary<object, Borrow> state)
    {
        if (Source(value, state) is { } borrow)
            state[holder] = borrow;
        else
            state.Remove(holder);
    }

    // Only the pattern's own declaration binds the tested value; a nested one binds a member of it.
    static ILocalSymbol? Declared(IPatternOperation pattern) =>
        pattern switch
        {
            IDeclarationPatternOperation { DeclaredSymbol: ILocalSymbol local } => local,
            IRecursivePatternOperation { DeclaredSymbol: ILocalSymbol local } => local,
            _ => null,
        };

    static void Invalidate(Dictionary<object, Borrow> state, int moves, string name)
    {
        foreach (var holder in state.Keys.ToArray())
        {
            var borrow = state[holder];
            if (borrow.StaleBy is null && (borrow.MovedBy & moves) != 0)
                state[holder] = borrow with { StaleBy = name };
        }
    }

    static Borrow? Source(IOperation operation, Dictionary<object, Borrow> state)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                case IFieldReferenceOperation { Instance: { } instance }:
                    operation = instance;
                    continue;
                case ILocalReferenceOperation local:
                    return state.TryGetValue(local.Local, out var held) ? held : null;
                case IFlowCaptureReferenceOperation capture:
                    return state.TryGetValue(capture.Id, out var captured) ? captured : null;
                case IInvocationOperation invocation
                    when MovedBy(
                        invocation.TargetMethod.ReturnType,
                        invocation.TargetMethod.RefKind
                    )
                        is var kinds
                            and not 0:
                    return new Borrow(kinds, null);
                case IPropertyReferenceOperation property
                    when MovedBy(property.Property.Type, property.Property.RefKind)
                        is var kinds
                            and not 0:
                    if (
                        Unwrap(property.Instance) is ILocalReferenceOperation owner
                        && state.TryGetValue(owner.Local, out var inner)
                    )
                        return inner;
                    return new Borrow(kinds, null);
                default:
                    return null;
            }
        }
    }

    static int MovedBy(ITypeSymbol type, RefKind byRef)
    {
        if (byRef != RefKind.None)
            return Stored(type);
        if (!Effects.IsBorrow(type))
            return 0;
        var kinds = 0;
        foreach (var argument in ((INamedTypeSymbol)type).TypeArguments)
            kinds |= Stored(argument);
        return kinds == 0 ? Effects.Moves : kinds;
    }

    static int Stored(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol parameter)
        {
            var kinds = 0;
            foreach (var constraint in parameter.ConstraintTypes)
                kinds |= Stored(constraint);
            return kinds;
        }
        if (Symbols.Is(type, Symbols.IRow) || Symbols.Implements(type, Symbols.IRow))
            return Effects.MovesRowBlocks;
        if (Symbols.Is(type, Symbols.IDeclared) || Symbols.Implements(type, Symbols.IDeclared))
            return Effects.MovesEntities;
        return 0;
    }

    static bool HoldsBorrow(ILocalSymbol local) =>
        local.RefKind != RefKind.None || Effects.IsBorrow(local.Type);

    static IOperation? Unwrap(IOperation? operation)
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
}
