using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Tralys.Ecs.Generators.Analyzers;

/// <summary>A <c>[MustDispose]</c> handle does its closing work only on dispose, so one that is neither disposed nor handed on loses that
/// work silently.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UndisposedScopeAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.UndisposedScope];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (!Effects.IsRuntime(start.Compilation))
                start.RegisterOperationAction(Analyze, OperationKind.Invocation);
        });
    }

    static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (invocation.Type is not { } type || Consequence(type) is not { } lost)
            return;
        if (HandedOn(invocation, new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default)))
            return;
        context.ReportDiagnostic(
            Diagnostic.Create(
                Diagnostics.UndisposedScope,
                invocation.Syntax.GetLocation(),
                type.Name,
                invocation.TargetMethod.Name,
                lost
            )
        );
    }

    static string? Consequence(ITypeSymbol type) =>
        Symbols.Attribute(type.OriginalDefinition, "Tralys.Ecs.Generated.MustDisposeAttribute")
            is { } attribute
        && attribute.ConstructorArguments.Length == 1
            ? attribute.ConstructorArguments[0].Value as string
            : null;

    static bool HandedOn(IOperation operation, HashSet<ILocalSymbol> followed)
    {
        var parent = operation.Parent;
        while (
            parent
                is IConversionOperation
                    or IParenthesizedOperation
                    or IConditionalOperation
                    or ISwitchExpressionArmOperation
                    or ISwitchExpressionOperation
        )
        {
            operation = parent;
            parent = parent.Parent;
        }
        switch (parent)
        {
            case IUsingOperation or IReturnOperation or IArgumentOperation:
                return true;
            case IInvocationOperation { TargetMethod.Name: "Dispose" } call:
                return call.Instance == operation;
            case IVariableInitializerOperation initializer:
                IOperation? declaration = initializer.Parent;
                while (declaration is IVariableDeclaratorOperation or IVariableDeclarationOperation)
                    declaration = declaration.Parent;
                return declaration
                        is IVariableDeclarationGroupOperation
                        {
                            Parent: IUsingOperation or IUsingDeclarationOperation
                        }
                    || initializer.Parent is IVariableDeclaratorOperation { Symbol: var local }
                        && LaterHandedOn(local, initializer, followed);
            case ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment
                when assignment.Value == operation:
                return LaterHandedOn(target.Local, assignment, followed);
            default:
                return false;
        }
    }

    static bool LaterHandedOn(ILocalSymbol local, IOperation from, HashSet<ILocalSymbol> followed)
    {
        if (!followed.Add(local))
            return false;
        var root = from;
        while (root.Parent is not null)
            root = root.Parent;
        var handOns = new HashSet<SyntaxNode>();
        var elsewhere = false;
        foreach (var operation in root.Descendants())
        {
            if (
                operation is ILocalReferenceOperation reference
                && SymbolEqualityComparer.Default.Equals(reference.Local, local)
                && HandedOn(reference, followed)
            )
            {
                handOns.Add(reference.Syntax);
                elsewhere |= Function(reference) != Function(from);
            }
        }
        if (handOns.Count == 0)
            return false;
        return elsewhere
            || Graph(root) is not { } graph
            || Start(graph, local, from.Syntax.Span) is not { } start
            || Settled(start.Graph, start.Block, start.Index, local, handOns);
    }

    static IOperation? Function(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation or ILocalFunctionOperation)
                return current;
        }
        return null;
    }

    static ControlFlowGraph? Graph(IOperation root) =>
        root switch
        {
            IMethodBodyOperation body => ControlFlowGraph.Create(body),
            IConstructorBodyOperation body => ControlFlowGraph.Create(body),
            IBlockOperation block => ControlFlowGraph.Create(block),
            _ => null,
        };

    static (ControlFlowGraph Graph, BasicBlock Block, int Index)? Start(
        ControlFlowGraph graph,
        ILocalSymbol local,
        TextSpan assigned
    )
    {
        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Operations.Length; i++)
            {
                if (
                    block
                        .Operations[i]
                        .DescendantsAndSelf()
                        .Any(operation =>
                            Assigns(operation, local) && operation.Syntax.Span.Contains(assigned)
                        )
                )
                    return (graph, block, i + 1);
            }
        }
        foreach (var function in graph.LocalFunctions)
        {
            if (
                Start(graph.GetLocalFunctionControlFlowGraph(function), local, assigned) is
                { } found
            )
                return found;
        }
        foreach (var block in graph.Blocks)
        {
            foreach (var operation in block.Operations)
            {
                foreach (
                    var lambda in operation
                        .DescendantsAndSelf()
                        .OfType<IFlowAnonymousFunctionOperation>()
                )
                {
                    if (
                        Start(
                            graph.GetAnonymousFunctionControlFlowGraph(lambda),
                            local,
                            assigned
                        ) is
                        { } found
                    )
                        return found;
                }
            }
        }
        return null;
    }

    static bool Assigns(IOperation operation, ILocalSymbol local) =>
        operation is ISimpleAssignmentOperation { Target: ILocalReferenceOperation target }
        && SymbolEqualityComparer.Default.Equals(target.Local, local);

    enum Step
    {
        None,
        HandedOn,
        Overwritten,
    }

    static Step Of(IOperation operation, ILocalSymbol local, HashSet<SyntaxNode> handOns)
    {
        var overwritten = false;
        foreach (var inner in operation.DescendantsAndSelf())
        {
            if (
                inner is ILocalReferenceOperation reference
                && SymbolEqualityComparer.Default.Equals(reference.Local, local)
                && handOns.Contains(reference.Syntax)
            )
                return Step.HandedOn;
            overwritten |= Assigns(inner, local);
        }
        return overwritten ? Step.Overwritten : Step.None;
    }

    // A path that throws owes nothing.
    static bool Settled(
        ControlFlowGraph graph,
        BasicBlock start,
        int index,
        ILocalSymbol local,
        HashSet<SyntaxNode> handOns
    )
    {
        var blocks = graph.Blocks;
        var scanned = new bool[blocks.Length];
        var work = new Stack<(BasicBlock Block, int From)>();
        work.Push((start, index));
        while (work.Count > 0)
        {
            var (block, from) = work.Pop();
            var step = Step.None;
            for (var i = from; i < block.Operations.Length && step == Step.None; i++)
                step = Of(block.Operations[i], local, handOns);
            if (step == Step.None && block.BranchValue is { } value)
                step = Of(value, local, handOns);
            if (step == Step.Overwritten)
                return false;
            if (step == Step.HandedOn)
                continue;
            foreach (var branch in new[] { block.FallThroughSuccessor, block.ConditionalSuccessor })
            {
                if (
                    branch
                    is null
                        or {
                            Semantics: ControlFlowBranchSemantics.Throw
                                or ControlFlowBranchSemantics.Rethrow
                                or ControlFlowBranchSemantics.StructuredExceptionHandling
                                or ControlFlowBranchSemantics.Error
                        }
                )
                    continue;
                var through = Step.None;
                foreach (var region in branch.FinallyRegions)
                {
                    for (
                        var ordinal = region.FirstBlockOrdinal;
                        ordinal <= region.LastBlockOrdinal && through == Step.None;
                        ordinal++
                    )
                    {
                        foreach (var operation in blocks[ordinal].Operations)
                        {
                            through = Of(operation, local, handOns);
                            if (through != Step.None)
                                break;
                        }
                    }
                    if (through != Step.None)
                        break;
                }
                if (through == Step.Overwritten)
                    return false;
                if (through == Step.HandedOn || branch.Destination is not { } next)
                    continue;
                if (next.Kind == BasicBlockKind.Exit)
                    return false;
                if (!scanned[next.Ordinal])
                {
                    scanned[next.Ordinal] = true;
                    work.Push((next, 0));
                }
            }
        }
        return true;
    }
}
