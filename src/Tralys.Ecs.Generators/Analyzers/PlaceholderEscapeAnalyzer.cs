using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tralys.Ecs.Generators.Analyzers;

/// <summary>An entity created through <c>Commands</c> taken from a parameter, a chunk view or a local holding either is a placeholder until
/// playback, so it must not outlive its buffer.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PlaceholderEscapeAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.PlaceholderEscapes];

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
        if (
            invocation.TargetMethod.Name != "Create"
            || !Symbols.Is(invocation.TargetMethod.ContainingType, Symbols.Commands)
        )
            return;
        if (!FromCommandsSource(Unwrap(invocation.Instance), Body(invocation)))
            return;
        var inQuery =
            context.ContainingSymbol is IMethodSymbol method && Symbols.Has(method, Symbols.Query);

        var use = Consumer(invocation);
        if (Escape(use, inQuery) is { } how)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.PlaceholderEscapes, use.Syntax.GetLocation(), how)
            );
            return;
        }

        ILocalSymbol? local = use switch
        {
            IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator } =>
                declarator.Symbol,
            ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } => target.Local,
            _ => null,
        };
        if (local is null || Body(invocation) is not { } body)
            return;

        foreach (var operation in body.Descendants())
        {
            if (
                Unwrap(operation) is not ILocalReferenceOperation reference
                || !SymbolEqualityComparer.Default.Equals(reference.Local, local)
            )
                continue;
            var consumer = Consumer(operation);
            if (
                consumer is ISimpleAssignmentOperation assignment
                && ReferenceEquals(Unwrap(assignment.Target), operation)
            )
                continue;
            if (Escape(consumer, inQuery) is { } escape)
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.PlaceholderEscapes,
                        consumer.Syntax.GetLocation(),
                        escape
                    )
                );
        }
    }

    static string? Escape(IOperation consumer, bool inQuery) =>
        consumer switch
        {
            IReturnOperation => "returned from the method",
            ISimpleAssignmentOperation { Target: var target }
                when Unwrap(target)
                    is IParameterReferenceOperation
                    {
                        Parameter: { RefKind: RefKind.Out or RefKind.Ref } parameter,
                    } =>
                $"returned through the {(parameter.RefKind == RefKind.Out ? "out" : "ref")} parameter '{parameter.Name}'",
            ISimpleAssignmentOperation
            {
                Target: var target,
                Parent: not IObjectOrCollectionInitializerOperation
            }
                when Unwrap(target)
                    is IFieldReferenceOperation
                        or IPropertyReferenceOperation
                        or IArrayElementReferenceOperation => "stored outside the method",
            IArgumentOperation { Parent: IInvocationOperation { TargetMethod: var method } }
                when inQuery
                    && !method.Locations.Any(l => l.IsInSource)
                    && method.ContainingNamespace?.ToDisplayString() != "Tralys.Ecs" =>
                $"passed to '{method.ContainingType.Name}.{method.Name}'",
            _ => null,
        };

    static bool FromCommandsSource(IOperation? instance, IOperation? body) =>
        instance switch
        {
            IParameterReferenceOperation => true,
            IPropertyReferenceOperation { Property.Name: "Commands", Instance.Type: { } owner } =>
                Symbols.Is(owner, Symbols.ChunkView),
            ILocalReferenceOperation reference when body is not null => body.Descendants()
                .OfType<IVariableDeclaratorOperation>()
                .FirstOrDefault(d =>
                    SymbolEqualityComparer.Default.Equals(d.Symbol, reference.Local)
                )
                ?.Initializer?.Value
                is { } value
                && FromCommandsSource(Unwrap(value), body),
            _ => false,
        };

    static IOperation Consumer(IOperation operation)
    {
        var current = operation;
        while (current.Parent is IConversionOperation or IParenthesizedOperation)
            current = current.Parent;
        return current.Parent ?? current;
    }

    static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;
        while (operation is IParenthesizedOperation parenthesized)
            operation = parenthesized.Operand;
        return operation;
    }

    static IOperation? Body(IOperation operation)
    {
        var current = operation;
        while (current.Parent is not null)
            current = current.Parent;
        return current;
    }
}
