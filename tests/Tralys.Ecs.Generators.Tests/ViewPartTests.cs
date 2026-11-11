using Microsoft.CodeAnalysis.Diagnostics;
using Tralys.Ecs.Generators.Analyzers;

namespace Tralys.Ecs.Generators.Tests;

public sealed class ViewPartTests
{
    [Test]
    public async Task A_part_on_a_struct_that_is_not_a_view_is_an_error()
    {
        var compilation = GeneratorHarness.Compile(
            "Parts",
            """
            namespace App;
            [Tralys.Ecs.Component] public partial struct Vitals { public int Hp; }
            [Tralys.Ecs.Component, Tralys.Ecs.Part<Vitals>] public partial struct Guild { public int Id; }
            """,
            []
        );
        var diagnostics = await compilation
            .WithAnalyzers([new ComponentShapeAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("TECS0035");
    }

    [Test]
    public async Task Two_view_parts_with_the_same_name_are_an_error()
    {
        GeneratorHarness.Compile(
            "RepeatedParts",
            """
            namespace App.Body { [Tralys.Ecs.Component] public partial struct Vitals { public int Hp; } }
            namespace App.Mind { [Tralys.Ecs.Component] public partial struct Vitals { public int Focus; } }
            namespace App
            {
                [Tralys.Ecs.View, Tralys.Ecs.Part<Body.Vitals>, Tralys.Ecs.Part<Mind.Vitals>]
                public readonly partial struct Patient;
            }
            """,
            [],
            out var diagnostics
        );

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("TECS0034");
    }
}
