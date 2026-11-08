using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tralys.Ecs.Generators.Analyzers;

/// <summary>A type name may be declared once across this compilation and every referenced assembly's component module.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DuplicateDeclarationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.DuplicateDeclaration];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var referenced = Referenced(start.Compilation);
            var local = new ConcurrentBag<(string Name, Location Location)>();

            start.RegisterSymbolAction(
                symbol =>
                {
                    var type = (INamedTypeSymbol)symbol.Symbol;
                    if (!Symbols.IsDeclared(type) || type.TypeParameters.Length > 0)
                        return;
                    local.Add(
                        (Symbols.Name(type), type.Locations.FirstOrDefault() ?? Location.None)
                    );
                },
                SymbolKind.NamedType
            );

            start.RegisterCompilationEndAction(end =>
            {
                foreach (var attribute in end.Compilation.Assembly.GetAttributes())
                {
                    if (
                        !Symbols.IsAttribute(attribute, Symbols.Instance)
                        || attribute.AttributeClass!.TypeArguments.FirstOrDefault()
                            is not { } closed
                    )
                        continue;
                    local.Add(
                        (
                            Symbols.Name(closed),
                            attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                                ?? Location.None
                        )
                    );
                }

                var claimed = new Dictionary<string, string>(referenced, StringComparer.Ordinal);
                foreach (
                    var entry in local
                        .OrderBy(e => e.Location.SourceTree?.FilePath)
                        .ThenBy(e => e.Location.SourceSpan.Start)
                )
                {
                    if (claimed.TryGetValue(entry.Name, out var other))
                        end.ReportDiagnostic(
                            Diagnostic.Create(
                                Diagnostics.DuplicateDeclaration,
                                entry.Location,
                                entry.Name,
                                other
                            )
                        );
                    else
                        claimed[entry.Name] = end.Compilation.AssemblyName ?? "this assembly";
                }
            });
        });
    }

    static Dictionary<string, string> Referenced(Compilation compilation)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            foreach (var attribute in assembly.GetAttributes())
            {
                if (
                    !Symbols.IsAttribute(
                        attribute,
                        "Tralys.Ecs.Generated.ComponentModuleManifestAttribute"
                    )
                    || attribute.ConstructorArguments.Length != 1
                )
                    continue;
                foreach (var value in attribute.ConstructorArguments[0].Values)
                {
                    if (value.Value is string name && !names.ContainsKey(name))
                        names[name] = assembly.Name;
                }
            }
        }
        return names;
    }
}
