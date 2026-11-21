using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Tralys.Ecs.Generators.Analyzers;

namespace Tralys.Ecs.Generators.Tests;

public sealed class ScopedHandleAnalyzerTests
{
    static readonly ImmutableArray<MetadataReference> References =
    [
        .. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "harness.references"))
            .Select(path => MetadataReference.CreateFromFile(path)),
    ];

    const string Prelude = """
        using Tralys.Ecs;

        namespace App;

        [Component] public partial struct Vitals { public int Hp; }

        public static class Uses
        {

        """;

    [Test]
    public async Task An_edit_that_is_not_disposed_is_an_error_and_a_using_edit_is_not()
    {
        var diagnostics = await Analyze(
            new UndisposedScopeAnalyzer(),
            """
                static void Lost(World world, Entity e) => world.Edit<Vitals>(e).Value.Hp -= 5;

                static void Kept(World world, Entity e)
                {
                    using var edit = world.Edit<Vitals>(e);
                    edit.Value.Hp -= 5;
                    using (var other = world.FindEdit<Vitals>(e))
                        if (other.HasValue)
                            other.Value.Hp += 1;
                }

                static ComponentEdit<Vitals> Passed(World world, Entity e) => world.Edit<Vitals>(e);
            }
            """
        );

        await Assert
            .That(diagnostics.Select(d => (d.Id, Line(d).Contains("Lost("))))
            .IsEquivalentTo([("TECS0032", true)]);
    }

    [Test]
    public async Task An_optional_read_used_after_a_structural_change_warns()
    {
        var diagnostics = await Analyze(
            new RefAcrossStructuralCallAnalyzer(),
            """
                static int Stale(World world, Entity e)
                {
                    var vitals = world.Find<Vitals>(e);
                    world.Remove<Vitals>(e);
                    return vitals.Value.Hp;
                }
            }
            """
        );

        await Assert.That(diagnostics.Select(d => d.Id)).IsEquivalentTo(["TECS0019"]);
    }

    [Test]
    public async Task A_ref_used_on_the_next_loop_pass_in_a_handler_or_in_a_lambda_after_a_structural_change_warns()
    {
        var diagnostics = await Analyze(
            new RefAcrossStructuralCallAnalyzer(),
            """
                static int Looped(World world, Entity e, Entity other)
                {
                    ref readonly var vitals = ref world.Get<Vitals>(e);
                    var sum = 0;
                    for (var i = 0; i < 2; i++)
                    {
                        sum += vitals.Hp;
                        world.Remove<Vitals>(other);
                    }
                    return sum;
                }

                static int Handled(World world, Entity e)
                {
                    ref readonly var vitals = ref world.Get<Vitals>(e);
                    try
                    {
                        world.Remove<Vitals>(e);
                        return 0;
                    }
                    catch
                    {
                        return vitals.Hp;
                    }
                }

                static System.Func<int> Deferred(World world, Entity e)
                {
                    using var untracked = world.Untracked();
                    return () =>
                    {
                        ref readonly var vitals = ref world.Get<Vitals>(e);
                        world.Remove<Vitals>(e);
                        return vitals.Hp + 1;
                    };
                }
            }
            """
        );

        await Assert
            .That(diagnostics.Select(d => Line(d).Trim()))
            .IsEquivalentTo(["sum += vitals.Hp;", "return vitals.Hp;", "return vitals.Hp + 1;"]);
    }

    static string Line(Diagnostic diagnostic) =>
        diagnostic.Location.SourceTree is { } tree
            ? tree.GetText()
                .Lines[diagnostic.Location.GetLineSpan().StartLinePosition.Line]
                .ToString()
            : $"(no source: {diagnostic.Id})";

    static async Task<ImmutableArray<Diagnostic>> Analyze(DiagnosticAnalyzer analyzer, string body)
    {
        var compilation = CSharpCompilation.Create(
            "App",
            [CSharpSyntaxTree.ParseText(Prelude + body)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
        );
        CSharpGeneratorDriver
            .Create(new EcsGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out _);
        return await generated.WithAnalyzers([analyzer]).GetAnalyzerDiagnosticsAsync();
    }
}
