namespace Tralys.Ecs.Generators.Tests;

public sealed class SideDataTests
{
    [Test]
    public async Task A_relation_passed_to_a_component_verb_does_not_compile()
    {
        var compilation = GeneratorHarness.Compile(
            "SideData",
            """
            namespace App;
            [Tralys.Ecs.Relation] public partial struct Follows;
            static class Use
            {
                public static bool Check(Tralys.Ecs.World world, Tralys.Ecs.Entity entity) => world.Has<Follows>(entity);
            }
            """,
            []
        );

        await Assert.That(compilation.GetDiagnostics().Select(d => d.Id)).Contains("CS0315");
    }
}
