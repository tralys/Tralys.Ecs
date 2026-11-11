using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tralys.Ecs.Generators.Analyzers;

/// <summary>Checks query bodies and collect predicates: none may need a settled world, a serial body or predicate may not change
/// structure, a parallel body may not change the world directly or use world scratch, and a <c>ref</c> component parameter should be
/// written.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class QueryBodyAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [
            Diagnostics.WorldWriteInParallelQuery,
            Diagnostics.StructuralWriteInQuery,
            Diagnostics.SettledApiInQuery,
            Diagnostics.OwnerThreadApiInParallelQuery,
            Diagnostics.RefParameterNeverWritten,
        ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationBlockStartAction(start =>
        {
            if (start.OwningSymbol is not IMethodSymbol method)
                return;
            bool parallel;
            if (Symbols.Attribute(method, Symbols.Query) is { } query)
                parallel = Symbols.Named(query, "Parallel", false);
            else if (Symbols.Has(method, Symbols.Collect))
                parallel = false;
            else
                return;
            start.RegisterOperationAction(
                operation => Body(operation, method, parallel),
                OperationKind.Invocation
            );
        });
        context.RegisterSyntaxNodeAction(Unwritten, SyntaxKind.MethodDeclaration);
    }

    static void Body(OperationAnalysisContext context, IMethodSymbol query, bool parallel)
    {
        var target = ((IInvocationOperation)context.Operation).TargetMethod;
        var name = target.ContainingType.Name + "." + target.Name;
        var location = context.Operation.Syntax.GetLocation();
        if (Effects.Of(target) is not { } effect)
        {
            if (
                Symbols.Has(target, Symbols.Structural)
                && !(parallel && target.Parameters.Any(p => Symbols.Is(p.Type, Symbols.Commands)))
            )
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.StructuralWriteInQuery,
                        location,
                        name,
                        query.Name,
                        parallel
                            ? "pass the query's Commands parameter in and record through it"
                            : ActFix
                    )
                );
            return;
        }
        if (effect.Has(Effects.Settled))
            context.ReportDiagnostic(
                Diagnostic.Create(Diagnostics.SettledApiInQuery, location, name, query.Name)
            );
        else if (parallel && effect.Has(Effects.Mutates))
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.WorldWriteInParallelQuery,
                    location,
                    name,
                    query.Name,
                    Recordable(target, context.Compilation)
                        ? "record it through the query's Commands parameter"
                        : "move it before or after the run, or make the query serial"
                )
            );
        else if (
            effect.Has(Effects.Structural)
            && !(
                parallel
                && (
                    Symbols.Is(target.ContainingType, Symbols.Commands)
                    || Symbols.Is(target.ContainingType, Symbols.CommandRows)
                )
            )
        )
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.StructuralWriteInQuery,
                    location,
                    name,
                    query.Name,
                    !parallel ? ActFix
                        : Recordable(target, context.Compilation)
                            ? "record it through the query's Commands parameter"
                        : "move it before or after the run"
                )
            );
        else if (parallel && effect.Has(Effects.OwnerThread))
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.OwnerThreadApiInParallelQuery,
                    location,
                    name,
                    query.Name
                )
            );
    }

    const string ActFix =
        "return true from a [Collect] predicate and make the change in its Act, which runs once the walk has closed";

    static bool Recordable(IMethodSymbol target, Compilation compilation) =>
        compilation.GetTypeByMetadataName(Symbols.Commands) is { } commands
        && !commands.GetMembers(target.Name).IsEmpty;

    static void Unwritten(SyntaxNodeAnalysisContext context)
    {
        var syntax = (MethodDeclarationSyntax)context.Node;
        if (
            context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken)
                is not { } method
            || !(Symbols.Has(method, Symbols.Query) || Symbols.Has(method, Symbols.Collect))
        )
            return;
        var flow = syntax switch
        {
            { Body: { } body } => context.SemanticModel.AnalyzeDataFlow(body),
            { ExpressionBody.Expression: { } expression } => context.SemanticModel.AnalyzeDataFlow(
                expression
            ),
            _ => null,
        };
        if (flow is not { Succeeded: true })
            return;
        foreach (var parameter in method.Parameters)
        {
            if (
                parameter.RefKind != RefKind.Ref
                || Symbols.Facts(parameter.Type) is null
                || flow.WrittenInside.Contains(parameter, SymbolEqualityComparer.Default)
            )
                continue;
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.RefParameterNeverWritten,
                    parameter.Locations.FirstOrDefault(),
                    parameter.Name,
                    method.Name
                )
            );
        }
    }
}
