using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tralys.Ecs.Generators.Analyzers;

/// <summary>A struct member that is not readonly, called through a readonly reference, runs on a hidden copy, so whatever it writes is
/// lost without a diagnostic from the compiler.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReadonlyReceiverAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.MutationThroughReadonlyReference];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(
            AnalyzeWrite,
            OperationKind.SimpleAssignment,
            OperationKind.CompoundAssignment,
            OperationKind.CoalesceAssignment,
            OperationKind.Increment,
            OperationKind.Decrement
        );
    }

    static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (
            invocation.Instance is { } receiver
            && Mutates(invocation.TargetMethod)
            && IsReadonly(receiver, context.ContainingSymbol)
        )
            Report(context, invocation.TargetMethod.Name, receiver);
    }

    static void AnalyzeWrite(OperationAnalysisContext context)
    {
        var target = context.Operation switch
        {
            IAssignmentOperation assignment => assignment.Target,
            IIncrementOrDecrementOperation step => step.Target,
            _ => null,
        };
        if (
            target
                is IPropertyReferenceOperation
                {
                    Property.SetMethod: { } setter,
                    Instance: { } receiver,
                } property
            && Mutates(setter)
            && IsReadonly(receiver, context.ContainingSymbol)
        )
            Report(context, property.Property.Name, receiver);
    }

    static void Report(OperationAnalysisContext context, string member, IOperation receiver) =>
        context.ReportDiagnostic(
            Diagnostic.Create(
                Diagnostics.MutationThroughReadonlyReference,
                context.Operation.Syntax.GetLocation(),
                member,
                receiver.Syntax.ToString()
            )
        );

    static bool Mutates(IMethodSymbol method) =>
        !method.IsStatic
        && !method.IsReadOnly
        && !method.IsImplicitlyDeclared
        && method.ContainingType is { IsValueType: true, IsReadOnly: false } type
        && !InSystem(type.ContainingNamespace);

    // The BCL marks what it does not mutate; what remains (ValueTuple.Equals and the like) would only report noise.
    static bool InSystem(INamespaceSymbol? space)
    {
        while (space is { IsGlobalNamespace: false })
        {
            if (space.ContainingNamespace.IsGlobalNamespace)
                return space.Name == "System";
            space = space.ContainingNamespace;
        }
        return false;
    }

    static bool IsReadonly(IOperation receiver, ISymbol? containing) =>
        receiver switch
        {
            ILocalReferenceOperation { Local.RefKind: RefKind.RefReadOnly } => true,
            IParameterReferenceOperation
            {
                Parameter.RefKind: RefKind.In or RefKind.RefReadOnlyParameter
            } => true,
            IInvocationOperation { TargetMethod.RefKind: RefKind.RefReadOnly } => true,
            IPropertyReferenceOperation { Property.RefKind: RefKind.RefReadOnly } => true,
            IFieldReferenceOperation field => field.Field.IsReadOnly
                && !Initializing(field.Field, containing)
                || field.Field.ContainingType.IsValueType
                    && field.Instance is { } instance
                    && IsReadonly(instance, containing),
            _ => false,
        };

    static bool Initializing(IFieldSymbol field, ISymbol? containing) =>
        containing is IMethodSymbol method
        && (
            method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
            || method.IsInitOnly
        )
        && method.IsStatic == field.IsStatic
        && SymbolEqualityComparer.Default.Equals(method.ContainingType, field.ContainingType);
}
