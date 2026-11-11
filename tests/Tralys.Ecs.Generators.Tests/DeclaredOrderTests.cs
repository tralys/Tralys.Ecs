namespace Tralys.Ecs.Generators.Tests;

public sealed class DeclaredOrderTests
{
    [Test]
    public async Task An_abstract_system_base_is_not_a_declared_system()
    {
        var compilation = GeneratorHarness.Compile(
            "Declared",
            """
            using Tralys.Ecs;
            namespace App;
            [SystemSet] public sealed class Play;
            [Schedule, Set<Play>] public sealed partial class Game;
            [System, InSet<Play>] public abstract partial class Base : SystemBase;
            [System, InSet<Play>]
            public sealed partial class Concrete : Base
            {
                protected override void OnRun(in RunContext context) { }
            }
            """,
            []
        );
        var declaration = compilation.SyntaxTrees.Single(t => t.FilePath.Contains("Game.Schedule"));
        var text = declaration.ToString();

        await Assert
            .That(
                (
                    text.Contains("typeof(global::App.Concrete)"),
                    text.Contains("typeof(global::App.Base)")
                )
            )
            .IsEqualTo((true, false));
    }
}
