using System.Text.RegularExpressions;

namespace Tralys.Ecs.Generators.Tests;

public sealed class LayoutKeyTests
{
    const string Prelude = """
        using Tralys.Ecs;
        namespace App;

        """;

    static ulong LayoutOf(string source)
    {
        var compilation = GeneratorHarness.Compile(
            "Layout",
            Prelude + source,
            [],
            out var diagnostics
        );
        if (diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            throw new InvalidOperationException(string.Join("; ", diagnostics));
        var module = compilation
            .SyntaxTrees.Select(t => t.ToString())
            .First(t => t.Contains("LayoutHash"));
        return ulong.Parse(
            Regex.Match(module, @"Name = ""App\.Probe"",\s*LayoutHash = (\d+)UL").Groups[1].Value
        );
    }

    [Test]
    public async Task A_layout_change_below_the_top_level_fields_changes_the_hash()
    {
        (string Before, string After)[] changes =
        [
            (
                "public struct Leash { public int Length; public Entity Holder; } [Component] public partial struct Probe { public Leash Leash; }",
                "public struct Leash { public Entity Holder; public int Length; } [Component] public partial struct Probe { public Leash Leash; }"
            ),
            (
                "[Component] public partial struct Probe { public float Value { get; set; } public int Gear { get; set; } }",
                "[Component] public partial struct Probe { public int Gear { get; set; } public float Value { get; set; } }"
            ),
            (
                "public struct Waypoint { public int X; public int Y; } [Component] public partial struct Probe { public BufferHandle<Waypoint> Path; }",
                "public struct Waypoint { public short X; public short Y; } [Component] public partial struct Probe { public BufferHandle<Waypoint> Path; }"
            ),
            (
                "[Component] public partial struct Probe { public Entity Who; }",
                "[Component] public partial struct Probe { [Capture(CapturePolicy.Skip)] public Entity Who; }"
            ),
        ];

        var unchanged = changes
            .Where(c => LayoutOf(c.Before) == LayoutOf(c.After))
            .Select(c => c.After);

        await Assert.That(unchanged).IsEmpty();
    }

    [Test]
    public async Task An_entity_in_an_auto_property_is_out_of_reach()
    {
        GeneratorHarness.Compile(
            "Layout",
            Prelude + "[Component] public partial struct Probe { public Entity Who { get; set; } }",
            [],
            out var diagnostics
        );

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("TECS0008");
    }
}
