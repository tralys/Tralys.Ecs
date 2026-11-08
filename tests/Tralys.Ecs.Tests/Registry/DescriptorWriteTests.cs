namespace Tralys.Ecs.Tests;

public sealed class DescriptorWriteTests
{
    [Test]
    public async Task Setting_a_relation_by_descriptor_throws_as_side_data()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        var follows = TestWorlds.Registry.Get<Follows>();

        await Assert
            .That(() => world.Set(entity, follows, new byte[follows.Size]))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("is a relation or rows type");
    }
}
