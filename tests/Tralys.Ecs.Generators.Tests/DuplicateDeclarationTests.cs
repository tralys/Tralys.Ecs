using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Tralys.Ecs.Generators.Analyzers;

namespace Tralys.Ecs.Generators.Tests;

public sealed class DuplicateDeclarationTests
{
    [Test]
    public async Task A_type_name_already_declared_by_a_referenced_assembly_is_an_error()
    {
        var library = GeneratorHarness.Compile(
            "Library",
            "namespace Shared; [Tralys.Ecs.Component] public partial struct Owned { public int V; }",
            []
        );
        using var image = new MemoryStream();
        var emitted = library.Emit(image);
        var reference = MetadataReference.CreateFromImage(image.ToArray());

        var consumer = GeneratorHarness.Compile(
            "Consumer",
            "namespace Shared; [Tralys.Ecs.Component] public partial struct Owned { public int V; }",
            [reference]
        );
        var diagnostics = await consumer
            .WithAnalyzers([new DuplicateDeclarationAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        await Assert.That(emitted.Success).IsTrue();
        await Assert.That(diagnostics.Select(d => d.Id)).Contains("TECS0002");
    }
}
