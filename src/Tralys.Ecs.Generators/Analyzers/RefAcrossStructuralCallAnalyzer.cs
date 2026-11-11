using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Tralys.Ecs.Generators.Analyzers;

/// <summary>A <c>ref</c> local, or an <c>Optional</c> local, taken from the world points into a column; a later structural change on that world can move or reallocate the
/// row, so a use after it may read another entity. Uses are checked along every control-flow path, loop back-edges and exception handlers
/// included. Query bodies are exempt: their structural changes are deferred.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RefAcrossStructuralCallAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.RefAcrossStructuralCall];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationBlockAction(Analyze);
    }

    readonly record struct Origin(ISymbol? Root, bool Exact);

    readonly record struct Borrow(ImmutableArray<Origin> Origins, string? StaleBy);

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

    static void Analyze(OperationBlockAnalysisContext context)
    {
        if (
            context.OwningSymbol is IMethodSymbol owner
            && (Symbols.Has(owner, Symbols.Query) || Symbols.Has(owner, Symbols.Collect))
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
                Walk(context.GetControlFlowGraph(block), findings);
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

    static void Walk(ControlFlowGraph graph, Findings findings)
    {
        var blocks = graph.Blocks;
        var entries = new Dictionary<ILocalSymbol, Borrow>?[blocks.Length];
        entries[0] = new(SymbolEqualityComparer.Default);
        var work = new Queue<int>();
        var queued = new bool[blocks.Length];
        work.Enqueue(0);
        queued[0] = true;
        while (work.Count > 0)
        {
            var index = work.Dequeue();
            queued[index] = false;
            var start = entries[index]!;
            var state = new Dictionary<ILocalSymbol, Borrow>(start, SymbolEqualityComparer.Default);
            Transfer(blocks[index], state, null);
            foreach (var (next, handler) in Successors(blocks[index]))
            {
                var changed = Merge(ref entries[next], state);
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
                    new Dictionary<ILocalSymbol, Borrow>(start, SymbolEqualityComparer.Default),
                    findings
                );
        }
        foreach (var function in graph.LocalFunctions)
            Walk(graph.GetLocalFunctionControlFlowGraph(function), findings);
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
                    Walk(graph.GetAnonymousFunctionControlFlowGraph(lambda), findings);
            }
        }
    }

    static IEnumerable<(int Ordinal, bool Handler)> Successors(BasicBlock block)
    {
        foreach (var branch in new[] { block.FallThroughSuccessor, block.ConditionalSuccessor })
        {
            if (branch is null)
                continue;
            if (branch.Destination is { } destination)
                yield return (destination.Ordinal, false);
            foreach (var region in branch.FinallyRegions)
                yield return (region.FirstBlockOrdinal, false);
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
                    yield return (handler.FirstBlockOrdinal, true);
            }
        }
    }

    static bool Merge(
        ref Dictionary<ILocalSymbol, Borrow>? into,
        Dictionary<ILocalSymbol, Borrow> from
    )
    {
        if (into is null)
        {
            into = new(from, SymbolEqualityComparer.Default);
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
            var origins = known.Origins;
            foreach (var origin in pair.Value.Origins)
            {
                if (!origins.Any(o => Same(o, origin)))
                    origins = origins.Add(origin);
            }
            var staleBy = known.StaleBy ?? pair.Value.StaleBy;
            if (origins.Length != known.Origins.Length || staleBy != known.StaleBy)
            {
                into[pair.Key] = new Borrow(origins, staleBy);
                changed = true;
            }
        }
        return changed;
    }

    static void Transfer(
        BasicBlock block,
        Dictionary<ILocalSymbol, Borrow> state,
        Findings? findings
    )
    {
        foreach (var operation in block.Operations)
            Visit(operation, state, findings);
        if (block.BranchValue is { } value)
            Visit(value, state, findings);
    }

    // Children run before their parent, so an argument read before a structural call is not a use after it.
    static void Visit(
        IOperation operation,
        Dictionary<ILocalSymbol, Borrow> state,
        Findings? findings
    )
    {
        switch (operation)
        {
            case IFlowAnonymousFunctionOperation:
                return;
            case ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment
                when HoldsRef(target.Local)
                    && (assignment.IsRef || target.Local.RefKind == RefKind.None):
                Visit(assignment.Value, state, findings);
                if (Source(assignment.Value) is { } origin)
                    state[target.Local] = new Borrow([origin], null);
                else
                    state.Remove(target.Local);
                return;
            case ILocalReferenceOperation reference:
                if (state.TryGetValue(reference.Local, out var borrow) && borrow.StaleBy is { } by)
                    findings?.Report(reference.Local, reference, by);
                return;
            case IInvocationOperation invocation:
                foreach (var child in invocation.ChildOperations)
                    Visit(child, state, findings);
                if (
                    Effects.Of(invocation.TargetMethod) is { } effect
                    && effect.Has(Effects.MovesRows)
                )
                    Invalidate(state, invocation);
                return;
        }
        foreach (var child in operation.ChildOperations)
            Visit(child, state, findings);
    }

    static void Invalidate(Dictionary<ILocalSymbol, Borrow> state, IInvocationOperation invocation)
    {
        var changed = OriginOf(invocation.Instance);
        var name = invocation.TargetMethod.ContainingType.Name + "." + invocation.TargetMethod.Name;
        foreach (var local in state.Keys.ToArray())
        {
            var borrow = state[local];
            if (borrow.StaleBy is null && borrow.Origins.Any(o => SameWorld(o, changed)))
                state[local] = borrow with { StaleBy = name };
        }
    }

    static Origin? Source(IOperation operation)
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
                case IPropertyReferenceOperation { Instance: { } optional }
                    when Symbols.Is(optional.Type, Symbols.Optional):
                    operation = optional;
                    continue;
                case IInvocationOperation invocation
                    when Effects.Of(invocation.TargetMethod) is { } effect
                        && effect.Has(Effects.Borrows):
                    return OriginOf(invocation.Instance);
                default:
                    return null;
            }
        }
    }

    static bool HoldsRef(ILocalSymbol local) =>
        local.RefKind != RefKind.None || Symbols.Is(local.Type, Symbols.Optional);

    static Origin OriginOf(IOperation? instance)
    {
        while (instance is IConversionOperation or IParenthesizedOperation)
            instance = instance switch
            {
                IConversionOperation conversion => conversion.Operand,
                IParenthesizedOperation parenthesized => parenthesized.Operand,
                _ => instance,
            };
        ISymbol? root = instance switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter => parameter.Parameter,
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation property => property.Property,
            _ => null,
        };
        return new Origin(root, instance?.Type is { } type && Symbols.Is(type, Symbols.World));
    }

    static bool Same(Origin a, Origin b) =>
        a.Exact == b.Exact && SymbolEqualityComparer.Default.Equals(a.Root, b.Root);

    // Two World-typed roots name the same world only if they are the same symbol; anything else may share a world.
    static bool SameWorld(Origin taken, Origin changed) =>
        !(taken.Exact && changed.Exact && taken.Root is not null && changed.Root is not null)
        || SymbolEqualityComparer.Default.Equals(taken.Root, changed.Root);
}
