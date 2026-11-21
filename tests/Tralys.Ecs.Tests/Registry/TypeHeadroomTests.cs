using Tralys.Ecs.ManyTypes.Fixture;

namespace Tralys.Ecs.Tests;

public sealed class TypeHeadroomTests
{
    [Test]
    public async Task A_type_past_index_1024_keeps_its_own_signature_bit()
    {
        var high = TestWorlds.Registry.Get<C1099>().Index;
        var aliased = high - 1024;
        using var world = TestWorlds.Create();

        var entity = world.Create();
        world.Set(entity, new C1099 { Value = 1 });
        var archetype = await Assert
            .That(world.allocator.Slots[entity.Index].Archetype)
            .IsNotNull();
        var signature = archetype.Signature;

        await Assert.That(high).IsGreaterThanOrEqualTo(1024);
        await Assert.That(signature.Has(high)).IsTrue();
        await Assert.That(signature.Has(aliased)).IsFalse();
    }
}
