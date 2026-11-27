using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tralys.Ecs.Generators.Analyzers;

/// <summary>Checks query bodies and collect predicates: a <c>ref</c> component parameter should be written.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class QueryBodyAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.RefParameterNeverWritten];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Unwritten, SyntaxKind.MethodDeclaration);
    }

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
