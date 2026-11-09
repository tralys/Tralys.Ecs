namespace Tralys.Ecs.Tests;

public sealed class BundleTests
{
    static readonly Tethered TetheredAt = new()
    {
        Anchored = new Anchored { Length = 1 },
        Position = new Position { X = 1 },
    };

    static readonly RareMover RareAt = new() { Position = new Position { X = 1 } };

    [Test]
    public async Task A_bundle_whose_requirement_follows_its_dependent_creates_when_deferred()
    {
        using var world = TestWorlds.Create();

        using (world.BeginDelivery())
            world.Create(TetheredAt);
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Create(TetheredAt);
        world.Submit(buffer);
        world.Playback();

        await Assert.That(Holders<Anchored>(world)).Count().IsEqualTo(2);
    }

    [Test]
    public async Task A_bundle_installs_its_sparse_tag_in_every_creation_mode()
    {
        using var world = TestWorlds.Create();

        world.Create(RareAt);
        using (world.BeginDelivery())
            world.Create(RareAt);
        var buffer = world.RentBuffer(1, 0);
        buffer.Commands.Create(RareAt);
        world.Submit(buffer);
        world.Playback();

        var holders = Holders<Position>(world);
        await Assert.That(holders).Count().IsEqualTo(3);
        await Assert.That(holders.All(entity => world.Has<Rare>(entity))).IsTrue();
    }

    static List<Entity> Holders<T>(World world)
        where T : struct, IComponent
    {
        var type = TestWorlds.Registry.Get<T>().Index;
        return
        [
            .. world
                .archetypes.Where(a => a.Signature.Has(type))
                .SelectMany(a => a.Entities.Take(a.Count)),
        ];
    }
}
