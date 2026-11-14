using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tralys.Ecs.Generators.Analyzers;

/// <summary>A run condition runs before every run of its system or set, so it must not capture state that changes what the schedule does
/// from one tick to the next without saying so.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RunConditionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.CapturingRunCondition];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (
            invocation.TargetMethod.Name != "RunIf"
            || !(
                Symbols.Is(invocation.TargetMethod.ContainingType, "Tralys.Ecs.SystemConfig")
                || Symbols.Is(invocation.TargetMethod.ContainingType, "Tralys.Ecs.SetConfig")
            )
            || invocation.Arguments.Length != 1
        )
            return;
        var value = invocation.Arguments[0].Value;
        if (Captures(value))
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.CapturingRunCondition, value.Syntax.GetLocation())
            );
    }

    // Anything not provably capture-free is reported: a local, a call result or a property may hold a closure.
    static bool Captures(IOperation value)
    {
        while (value is IConversionOperation conversion)
            value = conversion.Operand;
        return value switch
        {
            IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } => !lambda
                .Symbol
                .IsStatic,
            IDelegateCreationOperation { Target: IMethodReferenceOperation method } => !method
                .Method
                .IsStatic || method.Instance is not null,
            IConditionalOperation conditional => Captures(conditional.WhenTrue)
                || conditional.WhenFalse is null
                || Captures(conditional.WhenFalse),
            IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } } => false,
            ILiteralOperation or IDefaultValueOperation => false,
            _ => true,
        };
    }
}
