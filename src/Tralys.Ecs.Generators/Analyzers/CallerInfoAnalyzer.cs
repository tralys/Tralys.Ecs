using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tralys.Ecs.Generators.Analyzers;

/// <summary>The attributed log names the line that called a write, so an explicit value, an argument that lands on a caller-info
/// parameter by position, a forward of the caller's line without its file (or the reverse), or a helper that declares caller-info
/// parameters and forwards neither records the wrong call site.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CallerInfoAnalyzer : DiagnosticAnalyzer
{
    const string FilePath = "System.Runtime.CompilerServices.CallerFilePathAttribute";
    const string LineNumber = "System.Runtime.CompilerServices.CallerLineNumberAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.WrongCallSite];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (!IsEcsApi(method.ContainingType))
            return;
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is not { } parameter || CallerKind(parameter) is not { } kind)
                continue;
            var problem = argument.ArgumentKind switch
            {
                ArgumentKind.Explicit when Forwarded(argument.Value, kind) is null =>
                    $"is filled by the compiler; pass nothing, or forward a [{(kind == FilePath ? "CallerFilePath" : "CallerLineNumber")}] parameter of your own",
                ArgumentKind.DefaultValue when Unforwarded(invocation, kind) is { } own =>
                    $"is left to the compiler while the caller's {(kind == FilePath ? "line" : "file")} is forwarded; forward '{own.Name}' as well",
                ArgumentKind.DefaultValue
                    when OwnParameter(context.ContainingSymbol, kind) is { } own =>
                    $"is left to the compiler, so the log names this helper instead of its caller; forward '{own.Name}'",
                _ => null,
            };
            if (problem is null)
                continue;
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.WrongCallSite,
                    argument.Syntax.GetLocation(),
                    parameter.Name,
                    method.ContainingType.Name + "." + method.Name,
                    problem
                )
            );
        }
    }

    static IParameterSymbol? Unforwarded(IInvocationOperation invocation, string kind)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (
                argument.ArgumentKind == ArgumentKind.Explicit
                && argument.Parameter is { } parameter
                && CallerKind(parameter) is { } other
                && other != kind
                && Forwarded(argument.Value, other)?.ContainingSymbol is IMethodSymbol helper
            )
                return helper.Parameters.FirstOrDefault(p => CallerKind(p) == kind);
        }
        return null;
    }

    static IParameterSymbol? OwnParameter(ISymbol? containing, string kind) =>
        containing is IMethodSymbol helper
            ? helper.Parameters.FirstOrDefault(p => CallerKind(p) == kind)
            : null;

    static bool IsEcsApi(INamedTypeSymbol type) =>
        type.ContainingNamespace?.ToDisplayString() == "Tralys.Ecs"
        || Symbols.Has(type, Symbols.View);

    static string? CallerKind(IParameterSymbol parameter)
    {
        foreach (var attribute in parameter.GetAttributes())
        {
            var name = attribute.AttributeClass?.ToDisplayString();
            if (name is FilePath or LineNumber)
                return name;
        }
        return null;
    }

    static IParameterSymbol? Forwarded(IOperation value, string kind)
    {
        while (value is IConversionOperation conversion)
            value = conversion.Operand;
        return
            value is IParameterReferenceOperation reference
            && CallerKind(reference.Parameter) == kind
            ? reference.Parameter
            : null;
    }
}
