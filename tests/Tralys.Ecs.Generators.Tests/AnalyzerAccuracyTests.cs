using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    public async Task A_ref_held_across_a_helper_handed_the_world_is_flagged_and_one_across_a_helper_handed_only_what_cannot_move_storage_is_not()
    {
        var stale = await Stale(
            """
            public static class Uses
            {
                static void Hold(World world, Entity entity) => world.Set(entity, new Holder());

                static int Twice(int value) => value * 2;

                static int Count(WorldReader reader, EntityReader entity) =>
                    reader.Has<Score>(entity) && entity.Has<Score>() ? 1 : 0;

                static void Nudge(ComponentEdit<Score> score) => score.Value.Value++;

                public static int Moved(World world, Entity entity)
                {
                    ref readonly var held = ref world.Get<Score>(entity);
                    Hold(world, entity);
                    return held.Value;
                }

                public static int Kept(World world, Entity entity)
                {
                    ref readonly var kept = ref world.Get<Score>(entity);
                    var doubled = Twice(kept.Value);
                    return kept.Value + doubled;
                }

                public static int Read(World world, Entity entity)
                {
                    ref readonly var read = ref world.Get<Score>(entity);
                    var count = Count(world.Reader, new EntityReader(world.Reader, entity));
                    return read.Value + count;
                }

                public static int Edited(World world, Entity entity)
                {
                    using var edit = world.Edit<Score>(entity);
                    Nudge(edit);
                    return edit.Value.Value;
                }
            }
            """
        );

        await Assert.That(stale).IsEquivalentTo(["held"]);
    }

    [Test]
    public async Task A_ref_held_across_a_property_of_a_world_holder_is_kept_and_one_across_its_method_is_not()
    {
        var stale = await Stale(
            """
            public sealed class Clock(World world)
            {
                public int Now => world.EntityCount;

                public void Spawn() => world.Create();
            }

            public static class Uses
            {
                public static int Kept(World world, Entity entity, Clock clock)
                {
                    ref readonly var kept = ref world.Get<Score>(entity);
                    var now = clock.Now;
                    return kept.Value + now;
                }

                public static int Moved(World world, Entity entity, Clock clock)
                {
                    ref readonly var held = ref world.Get<Score>(entity);
                    clock.Spawn();
                    return held.Value;
                }
            }
            """
        );

        await Assert.That(stale).IsEquivalentTo(["held"]);
    }

    [Test]
    public async Task A_rows_move_invalidates_a_row_borrow_but_not_a_component_borrow()
    {
        var stale = await Stale(
            """
            [Rows] public partial struct Badge { [RowKey] public int Kind; }
            public static class Uses
            {
                public static int Kept(World world, Entity entity)
                {
                    ref readonly var score = ref world.Get<Score>(entity);
                    world.BadgeRows(entity).Set(new Badge { Kind = 1 });
                    return score.Value;
                }

                public static bool Moved(World world, Entity entity)
                {
                    var badges = world.BadgeRows(entity);
                    var found = badges.Find(1);
                    badges.Set(new Badge { Kind = 2 });
                    return found.HasValue;
                }
            }
            """
        );

        await Assert.That(stale).IsEquivalentTo(["found"]);
    }

    [Test]
    public async Task A_ref_from_a_view_property_used_after_a_structural_change_is_flagged()
    {
        var ids = await Analyze(
            new RefAcrossStructuralCallAnalyzer(),
            """
            [View, Part<Score>] public readonly partial struct Scored;
            public static class Uses
            {
                public static int Stale(World world, Entity entity)
                {
                    if (!new WorldEntity(world, entity).TryAs<Scored>(out var view))
                        return 0;
                    ref readonly var score = ref view.Score;
                    world.Set(entity, new Holder());
                    return score.Value;
                }
            }
            """
        );

        await Assert.That(ids).IsEquivalentTo(["TECS0019"]);
    }

    [Test]
    public async Task An_edit_disposed_by_using_after_a_structural_change_is_flagged()
    {
        var ids = await Analyze(
            new RefAcrossStructuralCallAnalyzer(),
            """
            [Component] public partial struct Mana { public int Value; }
            public static class Uses
            {
                public static void Stale(World world, Entity entity)
                {
                    using var edit = world.Edit<Mana>(entity);
                    edit.Value.Value = 1;
                    world.Set(entity, new Holder());
                }
            }
            """
        );

        await Assert.That(ids).IsEquivalentTo(["TECS0019"]);
    }

    [Test]
    public async Task An_edit_written_after_a_structural_change_is_flagged()
    {
        var ids = await Analyze(
            new RefAcrossStructuralCallAnalyzer(),
            """
            [Component] public partial struct Mana { public int Value; }
            public static class Uses
            {
                public static void Stale(World world, Entity entity)
                {
                    var edit = world.Edit<Mana>(entity);
                    world.Set(entity, new Holder());
                    edit.Value.Value = 99;
                    edit.Dispose();
                }
            }
            """
        );

        await Assert.That(ids).IsEquivalentTo(["TECS0019"]);
    }

    [Test]
    public async Task Reads_kernels_lookups_and_absent_callbacks_leave_a_borrow_valid()
    {
        var stale = await Stale(
            """
            public readonly struct Unit(WorldEntity entity)
            {
                public ref readonly Score Score => ref entity.Get<Score>();
            }

            public struct Tagger : IChunkKernel
            {
                public World World;
                public Entity Target;

                public void Execute(ref ChunkView chunk)
                {
                    ref readonly var tagged = ref World.Get<Score>(Target);
                    World.Set(Target, new Holder());
                    _ = tagged.Value;
                }
            }

            public static class Uses
            {
                public static int Summed(World world, Entity entity, System.Collections.Generic.IReadOnlyList<int> values)
                {
                    ref readonly var summed = ref world.Get<Score>(entity);
                    var total = 0;
                    for (var i = 0; i < values.Count; i++)
                        total += values[i];
                    return total + summed.Value;
                }

                public static int Paired(Unit first, Unit second)
                {
                    ref readonly var mine = ref first.Score;
                    ref readonly var theirs = ref second.Score;
                    return mine.Value + theirs.Value;
                }

                static int Peek(Lookup<Score> scores, Entity entity) => scores.Get(entity).Value;

                public static int Looked(World world, Entity entity, Entity other)
                {
                    ref readonly var looked = ref world.Get<Score>(entity);
                    var peeked = Peek(new Lookup<Score>(world), other);
                    return looked.Value + peeked;
                }

                static int Twice(int value, System.Func<int, int> map = null) => map is null ? value * 2 : map(value);

                public static int Defaulted(World world, Entity entity)
                {
                    ref readonly var defaulted = ref world.Get<Score>(entity);
                    return defaulted.Value + Twice(defaulted.Value) + Twice(defaulted.Value, null);
                }
            }
            """
        );

        await Assert.That(stale).IsEmpty();
    }

    [Test]
    public async Task A_move_reached_through_a_finally_a_conditional_ref_a_pattern_an_alias_or_a_deconstruction_is_flagged()
    {
        var stale = await Stale(
            """
            [Tag] public partial struct Marked;
            [Rows] public partial struct Badge { [RowKey] public int Kind; }

            public sealed class Clock(World world)
            {
                public int Value
                {
                    get => 0;
                    set => world.Create();
                }
            }

            public static class Uses
            {
                public static int Finally(World world, Entity entity)
                {
                    ref readonly var guarded = ref world.Get<Score>(entity);
                    try { _ = guarded.Value; }
                    finally { world.Set(entity, new Holder()); }
                    return guarded.Value;
                }

                public static int Batched(World world, Entity entity, Entity other)
                {
                    ref readonly var batched = ref world.Get<Score>(entity);
                    using (var batch = world.Batch(other))
                        batch.Add<Marked>();
                    return batched.Value;
                }

                public static int Picked(World world, Entity a, Entity b, bool first)
                {
                    ref readonly var picked = ref (first ? ref world.Get<Score>(a) : ref world.Get<Score>(b));
                    world.Set(a, new Holder());
                    return picked.Value;
                }

                public static int Matched(World world, Entity entity)
                {
                    var badges = world.BadgeRows(entity);
                    if (badges.Find(1) is { HasValue: true } found)
                    {
                        badges.Set(new Badge { Kind = 2 });
                        return found.Value.Kind;
                    }
                    return 0;
                }

                public static int Aliased(World world, Entity entity)
                {
                    var alias = world;
                    ref readonly var aliased = ref world.Get<Score>(entity);
                    alias.Set(entity, new Holder());
                    return aliased.Value;
                }

                public static int Shared(World left, World right, Entity entity)
                {
                    ref readonly var shared = ref left.Get<Score>(entity);
                    right.Set(entity, new Holder());
                    return shared.Value;
                }

                public static int Deconstructed(World world, Entity entity, Clock clock)
                {
                    ref readonly var deconstructed = ref world.Get<Score>(entity);
                    (clock.Value, _) = (1, 2);
                    return deconstructed.Value;
                }
            }
            """
        );

        await Assert
            .That(stale)
            .IsEquivalentTo([
                "guarded",
                "batched",
                "picked",
                "found",
                "aliased",
                "shared",
                "deconstructed",
            ]);
    }

    [Test]
    public async Task A_world_holder_from_a_referenced_assembly_is_seen_through_its_private_fields()
    {
        var library = GeneratorHarness.Compile(
            "ClockLib",
            """
            using Tralys.Ecs;
            namespace Lib;
            public sealed class Clock
            {
                World world;
                public void Bind(World bound) => world = bound;
                public void Spawn() => world.Create();
            }
            """,
            []
        );
        using var image = new MemoryStream();
        await Assert.That(library.Emit(image).Success).IsTrue();

        var compilation = GeneratorHarness.Compile(
            "Accuracy",
            Prelude
                + """
                public static class Uses
                {
                    public static int Moved(World world, Entity entity, Lib.Clock clock)
                    {
                        ref readonly var held = ref world.Get<Score>(entity);
                        clock.Spawn();
                        return held.Value;
                    }
                }
                """,
            [MetadataReference.CreateFromImage(image.ToArray())]
        );
        var diagnostics = await compilation
            .WithAnalyzers([new RefAcrossStructuralCallAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        await Assert.That(diagnostics.Select(d => d.Id)).IsEquivalentTo(["TECS0019"]);
    }

    [Test]
    public async Task An_edit_overwritten_before_its_dispose_is_an_error()
    {
        var ids = await Analyze(
            new UndisposedScopeAnalyzer(),
            """
            [Component] public partial struct Mana { public int Value; }
            public static class Uses
            {
                public static void Looped(World world, Entity[] entities)
                {
                    ComponentEdit<Mana> edit = default;
                    foreach (var entity in entities)
                    {
                        edit = world.Edit<Mana>(entity);
                        edit.Value.Value++;
                    }
                    edit.Dispose();
                }

                public static void Replaced(World world, Entity first, Entity second)
                {
                    var edit = world.Edit<Mana>(first);
                    edit.Value.Value++;
                    edit = world.Edit<Mana>(second);
                    edit.Value.Value++;
                    edit.Dispose();
                }

                public static void Guarded(World world, Entity entity)
                {
                    var edit = world.Edit<Mana>(entity);
                    try { edit.Value.Value++; }
                    finally { edit.Dispose(); }
                }
            }
            """
        );

        await Assert.That(ids).IsEquivalentTo(["TECS0032", "TECS0032"]);
    }

    [Test]
    public async Task On_the_runtime_a_wrong_or_missing_effect_declaration_is_an_error()
    {
        var flagged = await RuntimeClosure(
            """
            public sealed class World
            {
                [Effect(Effects.MovesEntities)]
                internal int moves;

                [Effect(Effects.Structural)]
                void CheckStructural() { }

                [Effect(Effects.Structural | Effects.MovesEntities)]
                public void Add()
                {
                    CheckStructural();
                    moves++;
                }

                [Effect(Effects.Structural)]
                public void Remove() => Add();

                public void Clear() => Add();
            }
            """
        );

        await Assert
            .That(flagged)
            .IsEquivalentTo([("TECS0047", "World.Remove()"), ("TECS0047", "World.Clear()")]);
    }

    [Test]
    public async Task On_the_runtime_an_inherited_implementation_a_partial_body_and_an_implicit_base_constructor_are_reached()
    {
        var flagged = await RuntimeClosure(
            """
            internal interface IStep { void Run(World world); }
            internal class Base { public void Run(World world) => world.moves++; }
            internal sealed class Inherits : Base, IStep { }

            internal class Defaulted { internal static World Shared; internal Defaulted() => Shared.moves++; }
            internal sealed class NoCtor : Defaulted { }

            public sealed partial class World
            {
                [Effect(Effects.MovesEntities)]
                internal int moves;

                [Effect(Effects.None)]
                public void ThroughInterface()
                {
                    IStep step = new Inherits();
                    step.Run(this);
                }

                [Effect(Effects.None)]
                public void ThroughPartial() => Bump();

                partial void Bump();

                [Effect(Effects.None)]
                public void ThroughImplicitConstructor() => _ = new NoCtor();
            }

            public sealed partial class World
            {
                partial void Bump() => moves++;
            }
            """
        );

        await Assert
            .That(flagged)
            .IsEquivalentTo([
                ("TECS0047", "World.ThroughInterface()"),
                ("TECS0047", "World.ThroughPartial()"),
                ("TECS0047", "World.ThroughImplicitConstructor()"),
            ]);
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

    static async Task<ImmutableArray<(string Id, string Method)>> RuntimeClosure(string types)
    {
        var compilation = CSharpCompilation.Create(
            "Runtime",
            [
                CSharpSyntaxTree.ParseText(
                    $$"""
                    namespace Tralys.Ecs.Generated
                    {
                        [System.Flags]
                        public enum Effects { None = 0, Structural = 4, MovesEntities = 8, Mutates = 16 }

                        [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Field)]
                        public sealed class EffectAttribute(Effects effects) : System.Attribute
                        {
                            public Effects Effects { get; } = effects;
                        }
                    }

                    namespace Tralys.Ecs
                    {
                        using Tralys.Ecs.Generated;

                        {{types}}
                    }
                    """
                ),
            ],
            [
                .. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "harness.references"))
                    .Where(path =>
                        !Path.GetFileName(path).StartsWith("Tralys.", StringComparison.Ordinal)
                    )
                    .Select(path => MetadataReference.CreateFromFile(path)),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var diagnostics = await compilation
            .WithAnalyzers([new EffectClosureAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Select(d => (d.Id, d.GetMessage().Split('\'')[1]))];
    }

    static async Task<ImmutableArray<string>> Analyze(DiagnosticAnalyzer analyzer, string source)
    {
        var compilation = GeneratorHarness.Compile("Accuracy", Prelude + source, []);
        var diagnostics = await compilation.WithAnalyzers([analyzer]).GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Select(d => d.Id)];
    }

    static async Task<ImmutableArray<string>> Stale(string source)
    {
        var compilation = GeneratorHarness.Compile("Accuracy", Prelude + source, []);
        var diagnostics = await compilation
            .WithAnalyzers([new RefAcrossStructuralCallAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Select(d => d.GetMessage().Split('\'')[1])];
    }

    static ImmutableArray<string> Generated(string source)
    {
        GeneratorHarness.Compile("Accuracy", Prelude + source, [], out var diagnostics);
        return [.. diagnostics.Select(d => d.Id)];
    }
}
