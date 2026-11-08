using Microsoft.CodeAnalysis.Diagnostics;
using Tralys.Ecs.Generators.Analyzers;

namespace Tralys.Ecs.Generators.Tests;

public sealed class DeclarationTests
{
    [Test]
    public async Task An_enableable_sparse_tag_is_an_error()
    {
        var diagnostics = await Analyze(
            "namespace App; [Tralys.Ecs.Tag, Tralys.Ecs.Enableable, Tralys.Ecs.Storage(Tralys.Ecs.StorageKind.Sparse)] public partial struct Stunned;"
        );

        await Assert.That(diagnostics).Contains("TECS0042");
    }

    [Test]
    public async Task A_row_key_outside_a_rows_type_is_an_error()
    {
        var diagnostics = await Analyze(
            "namespace App; [Tralys.Ecs.Component] public partial struct Slot { [Tralys.Ecs.RowKey] public int Kind; }"
        );

        await Assert.That(diagnostics).Contains("TECS0042");
    }

    [Test]
    public async Task Entities_inside_a_buffer_of_another_element_type_are_an_error()
    {
        GeneratorHarness.Compile(
            "Declarations",
            """
            namespace App;
            public struct Pair { public Tralys.Ecs.Entity Target; public int Weight; }
            public struct Inner { public Tralys.Ecs.BufferHandle<Pair> Pairs; }
            [Tralys.Ecs.Component] public partial struct Threat { public Inner Inner; }
            """,
            [],
            out var diagnostics
        );

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("TECS0005");
    }

    static async Task<IEnumerable<string>> Analyze(string source)
    {
        var compilation = GeneratorHarness.Compile("Declarations", source, []);
        var diagnostics = await compilation
            .WithAnalyzers([new ComponentShapeAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        return diagnostics.Select(d => d.Id);
    }
}
