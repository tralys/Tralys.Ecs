using System.Runtime.CompilerServices;

namespace Tralys.Ecs.Tests;

public sealed class DepartingValueTests
{
    sealed class Recorder(int type) : ICoreHook
    {
        public readonly List<int> Seen = [];

        public void OnRemoving(World world, Entity entity, int removed, ref byte value)
        {
            if (removed == type)
                Seen.Add(Unsafe.As<byte, Health>(ref value).Value);
        }
    }

    [Test]
    public async Task A_remove_hook_reads_the_departing_value_not_the_row_that_fills_its_hole()
    {
        using var world = TestWorlds.Create();
        var recorder = new Recorder(TestWorlds.Registry.Get<Health>().Index);
        world.coreHooks.Add(recorder);
        var first = world.Create();
        world.Set(first, new Health { Value = 1 });
        var last = world.Create();
        world.Set(last, new Health { Value = 2 });

        world.Remove<Health>(first);
        world.Destroy(last);
        world.Playback();

        await Assert
            .That(recorder.Seen)
            .IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    sealed class BufferProbe(int type) : ICoreHook
    {
        public bool Live;

        public void OnRemoving(World world, Entity entity, int removed, ref byte value)
        {
            if (removed == type)
                Live = world
                    .Buffers<int>()
                    .TryGet(Unsafe.As<byte, Waypoints>(ref value).Stops, out _);
        }
    }

    [Test]
    public async Task A_remove_hook_sees_the_stored_buffers_before_a_staged_copy_is_dropped()
    {
        using var world = TestWorlds.Create();
        var probe = new BufferProbe(TestWorlds.Registry.Get<Waypoints>().Index);
        world.coreHooks.Add(probe);
        var entity = world.Create();
        var stored = new Waypoints { Stops = world.Buffers<int>().Allocate(2) };
        world.Set(entity, stored);

        using (world.BeginDelivery())
        {
            world.SetEnabled<Waypoints>(entity, false);
            world.Set(entity, stored);
            world.Remove<Waypoints>(entity);
        }
        world.Playback();

        await Assert.That(probe.Live).IsTrue();
    }
}
