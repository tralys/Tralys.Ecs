using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Tralys.Ecs.Generators.Analyzers;

namespace Tralys.Ecs.Generators.Tests;

public sealed class AnalyzerAccuracyTests
{
    const string Prelude = """
        using System.Runtime.CompilerServices;
        using Tralys.Ecs;
        namespace App;
        [Component] public partial struct Score { public int Value; }
        [Component] public partial struct Holder { public Entity Child; }

        """;

    [Test]
    public async Task A_kernel_storing_what_its_chunk_commands_create_is_an_error()
    {
        var ids = await Analyze(
            new PlaceholderEscapeAnalyzer(),
            """
            public struct Spawner : IChunkKernel
            {
                public static Entity Last;

                public void Execute(ref ChunkView chunk)
                {
                    var commands = chunk.Commands;
                    Last = commands.Create();
                }
            }
            """
        );

        await Assert.That(ids).Contains("TECS0011");
    }

    [Test]
    public async Task A_placeholder_in_a_recorded_payload_is_not_an_escape()
    {
        var ids = await Analyze(
            new PlaceholderEscapeAnalyzer(),
            """
            public sealed partial class Adoptions
            {
                [Query(Parallel = true)]
                static void Adopt(Entity entity, in Score score, Commands commands)
                {
                    var child = commands.Create();
                    commands.Set(entity, new Holder { Child = child });
                }
            }
            """
        );

        await Assert.That(ids).DoesNotContain("TECS0011");
    }

    [Test]
    public async Task An_untracked_scope_that_is_not_disposed_is_an_error()
    {
        var ids = await Analyze(
            new UndisposedScopeAnalyzer(),
            """
            public static class Uses
            {
                public static void Lost(World world) => world.Untracked();
            }
            """
        );

        await Assert.That(ids).Contains("TECS0032");
    }

    [Test]
    public async Task A_settled_api_in_a_serial_query_body_is_an_error()
    {
        var ids = await Analyze(
            new QueryBodyAnalyzer(),
            """
            public sealed partial class Saves
            {
                public static World World = null!;

                [Query]
                static void Save(Entity entity, in Score score) => World.Snapshot();
            }
            """
        );

        await Assert.That(ids).Contains("TECS0038");
    }

    [Test]
    public async Task World_scratch_in_a_parallel_query_body_is_an_error()
    {
        var ids = await Analyze(
            new QueryBodyAnalyzer(),
            """
            public sealed partial class Sums
            {
                public static World World = null!;

                [Query(Parallel = true)]
                static void Sum(Entity entity, in Score score)
                {
                    using var buffer = World.Scratch.Rent<int>(4);
                }
            }
            """
        );

        await Assert.That(ids).Contains("TECS0043");
    }

    [Test]
    public async Task A_ref_taken_again_after_a_structural_change_is_not_flagged()
    {
        var ids = await Analyze(
            new RefAcrossStructuralCallAnalyzer(),
            """
            public static class Uses
            {
                public static int Retake(World world, Entity entity)
                {
                    ref readonly var score = ref world.Get<Score>(entity);
                    world.Set(entity, new Holder());
                    score = ref world.Get<Score>(entity);
                    return score.Value;
                }
            }
            """
        );

        await Assert.That(ids).DoesNotContain("TECS0019");
    }

    [Test]
    public async Task A_helper_that_declares_caller_info_and_forwards_none_is_flagged()
    {
        var ids = await Analyze(
            new CallerInfoAnalyzer(),
            """
            public static class Helpers
            {
                public static void Wrap(World world, Entity entity, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
                    world.Set(entity, new Score());
            }
            """
        );

        await Assert.That(ids).Contains("TECS0036");
    }

    [Test]
    public async Task A_playback_point_ordered_against_an_outsider_is_a_point_error()
    {
        var ids = Generated(
            """
            [SystemSet] public sealed class Play;
            public sealed class Outsider;
            [Schedule, Set<Play>, PlaybackPoint("Settle", After = typeof(Outsider))]
            public sealed partial class Game;
            """
        );

        await Assert.That(ids).Contains("TECS0037");
    }

    [Test]
    public async Task A_repeated_point_name_or_a_second_finalising_point_is_an_error()
    {
        var ids = Generated(
            """
            [SystemSet] public sealed class Play;
            [Schedule, Set<Play>, PlaybackPoint("Settle", FinalizesDestroys = true), PlaybackPoint("Settle"), PlaybackPoint("Late", FinalizesDestroys = true)]
            public sealed partial class Game;
            """
        );

        await Assert.That(ids.Count(id => id == "TECS0037")).IsEqualTo(2);
    }

    [Test]
    public async Task A_set_condition_that_can_capture_state_is_flagged()
    {
        var ids = await Analyze(
            new RunConditionAnalyzer(),
            """
            [SystemSet] public sealed class Play;
            public readonly record struct Tick(double Seconds) : ITickContext;
            public static class Gates
            {
                public static void Configure(ScheduleBuilder<Tick> builder, bool open) =>
                    builder
                        .ConfigureSet<Play>(s => s.RunIf((in RunContext _) => open))
                        .ConfigureSet<Play>(s => s.RunIf(static (in RunContext _) => true));
            }
            """
        );

        await Assert.That(ids).IsEquivalentTo(["TECS0030"]);
    }

    [Test]
    public async Task A_condition_handed_over_as_anything_but_a_static_delegate_is_flagged()
    {
        var ids = await Analyze(
            new RunConditionAnalyzer(),
            """
            [SystemSet] public sealed class Play;
            public readonly record struct Tick(double Seconds) : ITickContext;
            public sealed class Switch { public bool Open; }
            public static class Gates
            {
                static readonly RunCondition Shut = static (in RunContext _) => false;

                static bool IsOpen(this Switch gate, in RunContext _) => gate.Open;

                static bool Due(in RunContext _) => true;

                static RunCondition Make(bool open) => (in RunContext _) => open;

                public static void Configure(ScheduleBuilder<Tick> builder, Switch gate, bool open, bool which)
                {
                    RunCondition held = (in RunContext _) => open;
                    builder.ConfigureSet<Play>(s =>
                        s.RunIf(gate.IsOpen)
                            .RunIf(held)
                            .RunIf(Make(open))
                            .RunIf(which ? (in RunContext _) => open : static (in RunContext _) => true)
                            .RunIf(Shut)
                            .RunIf(Due)
                            .RunIf(which ? Shut : static (in RunContext _) => true)
                    );
                }
            }
            """
        );

        await Assert.That(ids).IsEquivalentTo(["TECS0030", "TECS0030", "TECS0030", "TECS0030"]);
    }

    static async Task<ImmutableArray<string>> Analyze(DiagnosticAnalyzer analyzer, string source)
    {
        var compilation = GeneratorHarness.Compile("Accuracy", Prelude + source, []);
        var diagnostics = await compilation.WithAnalyzers([analyzer]).GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Select(d => d.Id)];
    }

    static ImmutableArray<string> Generated(string source)
    {
        GeneratorHarness.Compile("Accuracy", Prelude + source, [], out var diagnostics);
        return [.. diagnostics.Select(d => d.Id)];
    }
}
