namespace Tralys.Ecs.Examples;

public sealed class Graveyard : IDestroyObserver
{
    public readonly List<Entity> Buried = [];

    public void OnDestroy(in ObserverContext context, Entity entity) => Buried.Add(entity);
}

public sealed class MovedPositions(World world)
{
    readonly CursorId cursor = world.Changes.Register(nameof(MovedPositions));

    public int Drain(List<Entity> moved)
    {
        var sample = world.Changes.Sample(cursor);
        foreach (var change in sample.Changed<Position>())
            moved.Add(change.Entity);
        world.Changes.Commit(cursor, sample.Until);
        return moved.Count;
    }
}

public static class Reactions
{
    public static ObserverId WatchDestroys(World world, Graveyard graveyard) =>
        world.Observers.OnDestroy(graveyard);

    public static void StopWatching(World world, ObserverId id) => world.Observers.Remove(id);
}
