using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tralys.Ecs.Generators.Tests;

static class GeneratorHarness
{
    static readonly ImmutableArray<MetadataReference> References =
    [
        .. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "harness.references"))
            .Select(path => MetadataReference.CreateFromFile(path)),
    ];

    public static Compilation Compile(string name, string source, MetadataReference[] extra) =>
        Compile(name, source, extra, out _);

    public static Compilation Compile(
        string name,
        string source,
        MetadataReference[] extra,
        out ImmutableArray<Diagnostic> diagnostics
    )
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            [.. References, .. extra],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
        );
        CSharpGeneratorDriver
            .Create(new EcsGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out diagnostics);
        return generated;
    }
}
