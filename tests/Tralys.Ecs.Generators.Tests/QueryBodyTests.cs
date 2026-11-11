using Microsoft.CodeAnalysis.Diagnostics;
using Tralys.Ecs.Generators.Analyzers;

namespace Tralys.Ecs.Generators.Tests;

public sealed class QueryBodyTests
{
    [Test]
    public async Task A_parallel_query_writing_what_it_reads_from_a_parent_is_an_error()
    {
        GeneratorHarness.Compile(
            "ParallelParentWrite",
            """
            using Tralys.Ecs;
            namespace App;
            [Component] public partial struct Heat { public int Value; }
            public sealed partial class Spread
            {
                [Query(Parallel = true)]
                static void Warm(Parent<Heat> parent, ref Heat heat) => heat.Value += parent.HasValue ? 1 : 0;
            }
            """,
            [],
            out var diagnostics
        );

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("TECS0020");
    }

    [Test]
    public async Task A_row_write_in_a_parallel_query_body_is_an_error()
    {
        var compilation = GeneratorHarness.Compile(
            "ParallelRows",
            """
            using Tralys.Ecs;
            namespace App;
            [Component] public partial struct Score { public int Value; }
            [Rows] public partial struct Badge { [RowKey] public int Kind; }
            public sealed partial class Awards
            {
                public static World World = null!;

                [Query(Parallel = true)]
                static void Award(Entity entity, in Score score) =>
                    World.BadgeRows(entity).Set(new Badge { Kind = score.Value });
            }
            """,
            []
        );
        var diagnostics = await compilation
            .WithAnalyzers([new QueryBodyAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        await Assert.That(diagnostics.Select(d => d.Id)).Contains("TECS0017");
    }

    [Test]
    public async Task A_snapshot_in_a_parallel_query_body_reports_the_settled_world_error()
    {
        var compilation = GeneratorHarness.Compile(
            "ParallelSnapshot",
            """
            using Tralys.Ecs;
            namespace App;
            [Component] public partial struct Score { public int Value; }
            public sealed partial class Saves
            {
                public static World World = null!;

                [Query(Parallel = true)]
                static void Save(Entity entity, in Score score) => World.Snapshot();
            }
            """,
            []
        );
        var diagnostics = await compilation
            .WithAnalyzers([new QueryBodyAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        await Assert.That(diagnostics.Select(d => d.Id)).IsEquivalentTo(new[] { "TECS0038" });
    }

    [Test]
    public async Task A_serial_body_changes_no_structure_through_the_world_a_view_a_helper_or_commands()
    {
        var compilation = GeneratorHarness.Compile(
            "SerialStructure",
            """
            using Tralys.Ecs;
            namespace App;
            [Component] public partial struct Score { public int Value; }
            [Component, Enableable] public partial struct Glow { public int Level; }
            [Tag] public partial struct Lit;
            [Rows] public partial struct Badge { [RowKey] public int Kind; }
            [View, Part<Score>, Part<Lit>(Optional = true)] public readonly partial struct Lamp;
            public sealed partial class Scores
            {
                [Structural]
                static void Light(World world, Entity entity) => world.Add<Lit>(entity);

                [Query]
                static void ByWorld(Entity entity, in Score score, World world)
                {
                    world.SetEnabled<Glow>(entity, false);
                    world.Edit<Score>(entity).Value.Value = 2;
                    Light(world, entity);
                }

                [Query]
                static void ByView(Entity entity, in Score score, World world)
                {
                    if (new WorldEntity(world, entity).TryAs<Lamp>(out var lamp))
                        lamp.AddLit();
                }

                [Query]
                static void ByCommands(Entity entity, in Score score, Commands commands)
                {
                    commands.SetEnabled<Glow>(entity, false);
                    commands.BadgeRows(entity).Set(new Badge { Kind = 1 });
                }
            }
            """,
            []
        );
        var diagnostics = await compilation
            .WithAnalyzers([new QueryBodyAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        await Assert
            .That(diagnostics.Select(d => d.Id))
            .IsEquivalentTo(["TECS0045", "TECS0045", "TECS0045", "TECS0045", "TECS0045"]);
    }

    [Test]
    public async Task A_commands_parameter_on_a_serial_body_is_an_error()
    {
        GeneratorHarness.Compile(
            "SerialCommands",
            """
            using Tralys.Ecs;
            namespace App;
            [Component] public partial struct Score { public int Value; }
            public sealed partial class Spawns
            {
                [Query]
                static void Spawn(in Score score, Commands commands) { }
            }
            """,
            [],
            out var diagnostics
        );

        await Assert.That(diagnostics.Select(d => d.Id)).IsEquivalentTo(["TECS0015"]);
    }

    [Test]
    public async Task A_structural_call_in_a_collect_predicate_is_an_error()
    {
        var compilation = GeneratorHarness.Compile(
            "PredicateStructure",
            """
            using Tralys.Ecs;
            namespace App;
            [Component] public partial struct Score { public int Value; }
            public sealed partial class Culls
            {
                [Collect(Act = nameof(Drop))]
                static bool Doomed(Entity entity, in Score score, World world)
                {
                    world.Destroy(entity);
                    return true;
                }

                static void Drop(Entity entity) { }
            }
            """,
            []
        );
        var diagnostics = await compilation
            .WithAnalyzers([new QueryBodyAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        await Assert.That(diagnostics.Select(d => d.Id)).IsEquivalentTo(["TECS0045"]);
    }
}
