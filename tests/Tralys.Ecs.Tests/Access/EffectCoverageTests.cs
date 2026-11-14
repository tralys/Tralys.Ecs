using System.Reflection;
using Tralys.Ecs.Generated;

namespace Tralys.Ecs.Tests;

public sealed class EffectCoverageTests
{
    [Test]
    public async Task Every_public_method_of_the_world_api_declares_its_effects()
    {
        Type[] types =
        [
            typeof(World),
            typeof(WorldEntity),
            typeof(Lookup<>),
            typeof(RefLookup<>),
            typeof(RefLookupExtensions),
            typeof(ViewLookup<>),
            typeof(Rows<,>),
            typeof(Rows<,>.Enumerator),
            typeof(CommandRows<,>),
            typeof(Commands),
            typeof(EntityBatch),
            typeof(BufferArena<>),
            typeof(ChangeTracking),
            typeof(WorldScratch),
        ];
        var missing = types
            .SelectMany(type =>
                type.GetMethods(
                    BindingFlags.Public
                        | BindingFlags.Instance
                        | BindingFlags.Static
                        | BindingFlags.DeclaredOnly
                )
            )
            .Where(method =>
                !method.IsSpecialName
                && method.Name is not ("Equals" or "GetHashCode" or "ToString")
                && method.GetCustomAttribute<EffectAttribute>() is null
            )
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToList();

        await Assert.That(missing).IsEmpty();
    }
}
