using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

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
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (invocation.Type is not { } type || Consequence(type) is not { } lost)
            return;
        if (HandedOn(invocation))
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

    static bool HandedOn(IOperation operation)
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
                        && LaterHandedOn(local, initializer);
            default:
                return false;
        }
    }

    static bool LaterHandedOn(ILocalSymbol local, IOperation from)
    {
        var root = from;
        while (root.Parent is not null)
            root = root.Parent;
        foreach (var operation in root.Descendants())
        {
            if (
                operation is ILocalReferenceOperation reference
                && SymbolEqualityComparer.Default.Equals(reference.Local, local)
                && HandedOn(reference)
            )
                return true;
        }
        return false;
    }
}
