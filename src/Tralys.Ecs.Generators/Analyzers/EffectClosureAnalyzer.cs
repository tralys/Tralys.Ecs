using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tralys.Ecs.Generators.Analyzers;

/// <summary>On the runtime, every public method of a type that declares effects states exactly what its body can reach, and code consumers
/// write is reached only through a seam that declares what it may do.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EffectClosureAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.EffectMismatch, Diagnostics.UndeclaredSeam];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(compilation =>
        {
            if (Effects.IsRuntime(compilation.Compilation))
                Runtime(compilation, new EffectGraph(compilation.Compilation));
        });
    }

    static void Runtime(CompilationAnalysisContext context, EffectGraph graph)
    {
        var covered = new List<IMethodSymbol>();
        foreach (var type in Types(context.Compilation.Assembly.GlobalNamespace))
        {
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (
                    field.Type is not IFunctionPointerTypeSymbol pointer
                    || Effects.Declared(field) is not { } seam
                )
                    continue;
                Pointer(context, graph, field, pointer, seam);
                covered.AddRange(graph.PointedAt(pointer));
            }
            if (!EffectGraph.Visible(type) || !Declares(type))
                continue;
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (
                    method.IsImplicitlyDeclared
                    || !EffectGraph.Visible(method)
                    || method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                )
                    continue;
                covered.Add(method);
                Check(context, graph, method);
            }
        }
        Seams(context, graph, covered);
    }

    static bool Declares(INamedTypeSymbol type) =>
        type.GetMembers()
            .OfType<IMethodSymbol>()
            .Any(m => m.MethodKind == MethodKind.Ordinary && Effects.Declared(m) is not null);

    static void Check(CompilationAnalysisContext context, EffectGraph graph, IMethodSymbol method)
    {
        var declared = Effects.Declared(method);
        var location = method.Locations.FirstOrDefault() ?? Location.None;
        var required = method.MethodKind == MethodKind.Ordinary;
        if (method.IsAbstract)
        {
            if ((declared ?? (required ? null : Effects.None)) is not { } seam)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.UndeclaredSeam,
                        location,
                        Signature(method),
                        "is implemented by consumers and declares no [Effect]; declare what any implementation may do"
                    )
                );
                return;
            }
            foreach (var implementation in graph.Implementations(method))
            {
                var reached = graph.Closure(implementation);
                if ((reached & ~seam) == 0)
                    continue;
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.UndeclaredSeam,
                        implementation.Locations.FirstOrDefault() ?? location,
                        Signature(implementation),
                        $"implements '{Signature(method)}', which declares [Effect({Effects.Describe(seam)})], but its body can reach "
                            + Effects.Describe(reached)
                            + ": "
                            + graph.Explain(implementation, reached & ~seam)
                    )
                );
            }
            return;
        }
        var computed = graph.Closure(method);
        if (declared == computed || (declared is null && !required && computed == Effects.None))
            return;
        var stated = declared is { } flags ? $"[Effect({Effects.Describe(flags)})]" : "no [Effect]";
        var detail = computed == Effects.None ? "" : ": " + graph.Explain(method, computed);
        if (declared is { } extra && (extra & ~computed) != 0)
            detail += $"; nothing it reaches is {Effects.Describe(extra & ~computed)}";
        context.ReportDiagnostic(
            Diagnostic.Create(
                Diagnostics.EffectMismatch,
                location,
                Signature(method),
                stated,
                Effects.Describe(computed),
                detail
            )
        );
    }

    static void Pointer(
        CompilationAnalysisContext context,
        EffectGraph graph,
        IFieldSymbol field,
        IFunctionPointerTypeSymbol pointer,
        int seam
    )
    {
        foreach (var target in graph.PointedAt(pointer))
        {
            var reached = graph.Closure(target);
            if ((reached & ~seam) == 0)
                continue;
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.UndeclaredSeam,
                    field.Locations.FirstOrDefault() ?? Location.None,
                    EffectGraph.Name(field),
                    $"declares [Effect({Effects.Describe(seam)})], but '&{EffectGraph.Name(target)}', which it may point at, can reach "
                        + Effects.Describe(reached)
                        + ": "
                        + graph.Explain(target, reached & ~seam)
                )
            );
        }
    }

    static void Seams(
        CompilationAnalysisContext context,
        EffectGraph graph,
        List<IMethodSymbol> covered
    )
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in covered.OrderBy(EffectGraph.Name, StringComparer.Ordinal))
        {
            if (method.IsAbstract)
                continue;
            foreach (var reached in graph.Reach(method))
            {
                foreach (var seam in graph.NodeOf(reached).Seams)
                {
                    if (!reported.Add(seam.Name + "@" + seam.Location))
                        continue;
                    var path = graph.Path(
                        EffectGraph.Definition(method),
                        node => node.Seams.Contains(seam) ? "unknown effect" : null
                    );
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.UndeclaredSeam,
                            seam.Location,
                            seam.Name,
                            $"runs code this compilation cannot see, reached through {path}; declare what it may do with [Effect]"
                        )
                    );
                }
            }
        }
    }

    static string Signature(IMethodSymbol method) =>
        method.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

    static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol space)
            {
                foreach (var type in Types(space))
                    yield return type;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in Types(type))
                    yield return nested;
            }
        }
    }
}
