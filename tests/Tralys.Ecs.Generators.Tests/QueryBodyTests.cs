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
    public async Task A_parallel_body_cannot_take_the_world_or_a_world_entity_but_reads_through_a_reader()
    {
        GeneratorHarness.Compile(
            "ParallelWorld",
            """
            using Tralys.Ecs;
            namespace App;
            [Component] public partial struct Score { public int Value; }
            public sealed partial class Scans
            {
                [Query(Parallel = true)]
                static void ByWorld(in Score score, World world) { }

                [Query(Parallel = true)]
                static void ByEntity(in Score score, [Arg] WorldEntity owner) { }

                [Query(Parallel = true)]
                static void ByReader(Entity entity, in Score score, WorldReader reader) => _ = reader.Has<Score>(entity);

                [Query]
                static void Serially(in Score score, World world) { }
            }
            """,
            [],
            out var diagnostics
        );

        await Assert
            .That(diagnostics.Select(d => (d.Id, d.GetMessage().Split('\'')[1])))
            .IsEquivalentTo([("TECS0015", "world"), ("TECS0015", "owner")]);
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
    public async Task A_parallel_body_may_take_two_readers()
    {
        var compilation = GeneratorHarness.Compile(
            "TwoReaders",
            """
            using Tralys.Ecs;
            namespace App;
            [Component] public partial struct Score { public int Value; }
            public sealed partial class Scans
            {
                [Query(Parallel = true)]
                static void Compare(in Score score, WorldReader mine, WorldReader theirs) { }
            }
            """,
            [],
            out _
        );

        await Assert
            .That(
                compilation
                    .GetDiagnostics()
                    .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    .Select(d => d.Id)
            )
            .IsEmpty();
    }
}
