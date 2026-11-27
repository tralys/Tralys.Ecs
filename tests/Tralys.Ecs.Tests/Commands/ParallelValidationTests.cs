namespace Tralys.Ecs.Tests;

public sealed class ParallelValidationTests
{
    [Test]
    public async Task A_buffered_add_missing_its_requirement_is_dropped_and_reported()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Set(entity, new Anchored { Length = 1 });
        world.Submit(buffer);

        await Assert.That(() => world.Playback()).Throws<InvalidOperationException>();
        await Assert.That(world.Has<Anchored>(entity)).IsFalse();
    }

    [Test]
    public async Task Two_buffered_holders_of_a_sparse_singleton_tag_keep_only_the_first()
    {
        using var world = TestWorlds.Create();
        var first = world.Create();
        var second = world.Create();
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Set(first, default(Leader));
        buffer.Commands.Set(second, default(Leader));
        world.Submit(buffer);

        await Assert.That(() => world.Playback()).Throws<InvalidOperationException>();
        await Assert.That(world.Has<Leader>(first)).IsTrue();
        await Assert.That(world.Has<Leader>(second)).IsFalse();
    }

    [Test]
    public async Task A_buffered_self_parenting_is_dropped_without_replaying_its_batch()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        var follower = world.Create();
        var buffer = world.RentBuffer(1, 0);
        var made = buffer.Commands.Create();
        buffer.Commands.Set(made, new Health { Value = 1 });
        buffer.Commands.SetParent(entity, entity);
        buffer.Commands.Relate(follower, entity, new Follows());
        world.Submit(buffer);

        await Assert.That(() => world.Playback()).Throws<InvalidOperationException>();
        world.Playback();
        var healthy = 0;
        foreach (var _ in world.Query(QueryDesc.Builder().Read<Health>().Build()))
            healthy++;

        await Assert
            .That((healthy, world.Parent(entity), world.HasRelation<Follows>(follower, entity)))
            .IsEqualTo((1, default(Entity), true));
    }

    [Test]
    public async Task A_buffered_create_destroyed_after_taking_a_child_takes_the_child_with_it()
    {
        using var world = TestWorlds.Create();
        var child = world.Create();
        var buffer = world.RentBuffer(1, 0);
        var parent = buffer.Commands.Create();
        buffer.Commands.SetParent(child, parent);
        buffer.Commands.Destroy(parent);
        world.Submit(buffer);

        world.Playback();

        await Assert.That(world.Exists(child)).IsFalse();
    }
}
