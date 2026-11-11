using Microsoft.CodeAnalysis.Diagnostics;
using Tralys.Ecs.Generators.Analyzers;

namespace Tralys.Ecs.Generators.Tests;

public sealed class ReadonlyReceiverTests
{
    const string Cooldowns = """
        namespace App;
        public struct Gate
        {
            public int Deadline;
            public void Reset() => Deadline = 0;
            public readonly bool Open(int now) => now >= Deadline;
        }
        public struct Cooldowns { public Gate XpTick; }
        public sealed class Player
        {
            Cooldowns cooldowns;
            public ref readonly Cooldowns Cooldowns => ref cooldowns;
        }
        """;

    [Test]
    public async Task A_mutating_call_on_a_field_reached_through_a_ref_readonly_property_is_an_error()
    {
        var ids = await AnalyzerIds(
            "ReadonlyChainMutation",
            Cooldowns
                + """

                public static class Arm
                {
                    public static void XpTick(Player player) => player.Cooldowns.XpTick.Reset();
                }
                """
        );

        await Assert.That(ids).IsEquivalentTo(["TECS0046"]);
    }

    [Test]
    public async Task A_readonly_member_reached_through_the_same_chain_is_not_reported()
    {
        var ids = await AnalyzerIds(
            "ReadonlyChainRead",
            Cooldowns
                + """

                public static class Check
                {
                    public static bool XpTick(Player player) => player.Cooldowns.XpTick.Open(1);
                }
                """
        );

        await Assert.That(ids).IsEmpty();
    }

    static async Task<string[]> AnalyzerIds(string name, string source)
    {
        var compilation = GeneratorHarness.Compile(name, source, []);
        var diagnostics = await compilation
            .WithAnalyzers([new ReadonlyReceiverAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Select(d => d.Id)];
    }
}
