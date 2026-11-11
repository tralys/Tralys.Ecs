using Microsoft.CodeAnalysis.Diagnostics;
using Tralys.Ecs.Generators.Analyzers;

namespace Tralys.Ecs.Generators.Tests;

public sealed class CallerInfoTests
{
    [Test]
    public async Task An_explicit_call_site_is_flagged_and_a_forwarded_one_is_not()
    {
        var compilation = GeneratorHarness.Compile(
            "CallerInfo",
            """
            using System.Runtime.CompilerServices;
            using Tralys.Ecs;
            namespace App;
            [Component] public partial struct Vitals { public int Hp; }
            public static class Helpers
            {
                public static void Literal(World world, Entity entity) =>
                    world.Set(entity, new Vitals(), "Combat.cs", 12);

                public static void Forward(World world, Entity entity, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
                    world.Set(entity, new Vitals(), file: file, line: line);
            }
            """,
            []
        );
        var diagnostics = await compilation
            .WithAnalyzers([new CallerInfoAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        var flagged = diagnostics
            .Where(d => d.Id == "TECS0036")
            .Select(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .ToList();
        await Assert.That(flagged).IsEquivalentTo([7, 7]);
    }

    [Test]
    public async Task A_line_forwarded_without_the_file_is_flagged_only_where_the_helper_has_a_file()
    {
        var compilation = GeneratorHarness.Compile(
            "CallerInfoPair",
            """
            using System.Runtime.CompilerServices;
            using Tralys.Ecs;
            namespace App;
            [Component] public partial struct Vitals { public int Hp; }
            public static class Helpers
            {
                public static void LineOnly(World world, Entity entity, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
                    world.Set(entity, new Vitals(), line: line);

                public static void NoFile(World world, Entity entity, [CallerLineNumber] int line = 0) =>
                    world.Set(entity, new Vitals(), line: line);
            }
            """,
            []
        );
        var diagnostics = await compilation
            .WithAnalyzers([new CallerInfoAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        var flagged = diagnostics
            .Where(d => d.Id == "TECS0036")
            .Select(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .ToList();
        await Assert.That(flagged).IsEquivalentTo([7]);
    }
}
