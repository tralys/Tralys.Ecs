namespace Tralys.Ecs.Examples;

public static class Structure
{
    public static Entity SpawnSquad(World world, int size)
    {
        var leader = world.Create();
        for (var i = 0; i < size; i++)
        {
            var member = world.Create();
            world.SetParent(member, leader);
            world.Relate(member, leader, new Targets { Priority = i });
        }
        return leader;
    }

    public static int CountMembers(World world, Entity leader)
    {
        var count = 0;
        foreach (var _ in world.Children(leader))
            count++;
        return count;
    }

    public static int PriorityOf(World world, Entity member, Entity target) =>
        world.TryGetRelation<Targets>(member, target, out var edge) ? edge.Priority : -1;

    public static void Retarget(World world, Entity member, Entity target) =>
        world.Relate(member, target, new Targets { Priority = 0 });

    public static bool StandDown(World world, Entity member)
    {
        foreach (var target in world.Targets<Targets>(member))
            return world.Unrelate<Targets>(member, target);
        return false;
    }

    public static void Stock(World world, Entity entity, int slot, int count)
    {
        var items = world.ItemRows(entity);
        var held = items.TryGet(slot, out var item) ? item.Count : 0;
        items.Set(new Item { Slot = slot, Count = held + count });
    }

    public static void Disband(World world, Entity leader)
    {
        world.Destroy(leader);
        world.Playback();
    }
}
