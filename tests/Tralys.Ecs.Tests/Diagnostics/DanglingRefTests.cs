namespace Tralys.Ecs.Tests;

public sealed class DanglingRefTests
{
    sealed class Collect : IReferenceFaultSink
    {
        public readonly List<DanglingReference> Found = [];

        public void OnDanglingReference(in DanglingReference reference) => Found.Add(reference);
    }

    [Test]
    public async Task Dead_handles_in_nested_generic_and_nullable_fields_are_reported_after_playback()
    {
        var sink = new Collect();
        using var world = TestWorlds.Create(new WorldOptions { VerifyReferences = true });
        world.ReferenceFaults = sink;
        var holder = world.Create();
        var target = world.Create();
        world.Set(
            holder,
            new Tether
            {
                Strength = 1,
                Leash = new Leash { Length = 2, Holder = target },
            }
        );
        world.Set(holder, new Sent<Link> { Payload = new Link { Target = target } });
        world.Set(holder, new MaybeLink { Target = target });
        world.Playback();
        var whileAlive = sink.Found.Count;

        world.Destroy(target);
        world.Playback();

        await Assert.That(whileAlive).IsEqualTo(0);
        await Assert.That(sink.Found.All(f => f.Holder == holder && f.Target == target)).IsTrue();
        await Assert
            .That(sink.Found.Select(f => f.Type))
            .IsEquivalentTo([
                world.Registry.Get<Tether>(),
                world.Registry.Get<Sent<Link>>(),
                world.Registry.Get<MaybeLink>(),
            ]);
        await Assert
            .That(sink.Found.First(f => f.Type == world.Registry.Get<Tether>()).Field)
            .IsEqualTo(nameof(Tether.Leash));
    }

    [Test]
    public async Task A_dead_entity_left_in_an_entity_buffer_is_reported_with_its_element()
    {
        using var world = TestWorlds.Create();
        var holder = world.Create();
        var live = world.Create();
        var dead = world.Create();
        var members = world.Buffers<Entity>().Allocate(2);
        world.Buffers<Entity>().Get(members)[0] = live;
        world.Buffers<Entity>().Get(members)[1] = dead;
        world.Set(holder, new Roster { Members = members });
        world.Destroy(dead);
        world.Playback();

        var found = new List<DanglingReference>();
        world.FindDanglingReferences(found);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert
            .That(found[0])
            .IsEqualTo(
                new DanglingReference(
                    holder,
                    world.Registry.Get<Roster>(),
                    ReferenceHolder.Component,
                    0,
                    1,
                    nameof(Roster.Members),
                    dead
                )
            );
    }
}
