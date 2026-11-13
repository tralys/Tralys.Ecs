namespace Tralys.Ecs;

/// <summary>Routes a changed input's holder to the entities that owe work for it; static, so a route cannot capture state.</summary>
public interface IHop
{
    /// <summary>Adds each entity <paramref name="holder"/>'s change reaches; called only while the holder exists.</summary>
    static abstract void Route(World world, Entity holder, List<Entity> into);
}

/// <summary>Where a watched input's change is marked: on the entity that changed, or on the entities a route reaches from it.</summary>
public readonly struct Hop
{
    internal readonly Action<World, Entity, List<Entity>>? Route;

    Hop(Action<World, Entity, List<Entity>> route) => Route = route;

    /// <summary>The entity that changed.</summary>
    public static Hop Self => default;

    /// <summary>The changed entity's parent.</summary>
    public static Hop Parent => Via<ParentHop>();

    /// <summary>Every source with an edge of <typeparamref name="R"/> to the changed entity.</summary>
    public static Hop Sources<R>()
        where R : struct, IRelation => Via<SourcesHop<R>>();

    /// <summary>Every source with an edge of <typeparamref name="R"/> to any child of the changed entity.</summary>
    public static Hop ChildrenThenSources<R>()
        where R : struct, IRelation => Via<ChildrenThenSourcesHop<R>>();

    /// <summary>The entities <typeparamref name="THop"/> routes the changed entity to.</summary>
    public static Hop Via<THop>()
        where THop : IHop => new(Routes<THop>.Route);

    static class Routes<THop>
        where THop : IHop
    {
        public static readonly Action<World, Entity, List<Entity>> Route = THop.Route;
    }

    readonly struct ParentHop : IHop
    {
        public static void Route(World world, Entity holder, List<Entity> into) =>
            into.Add(world.Parent(holder));
    }

    readonly struct SourcesHop<R> : IHop
        where R : struct, IRelation
    {
        public static void Route(World world, Entity holder, List<Entity> into)
        {
            foreach (var source in world.Sources<R>(holder))
                into.Add(source);
        }
    }

    readonly struct ChildrenThenSourcesHop<R> : IHop
        where R : struct, IRelation
    {
        public static void Route(World world, Entity holder, List<Entity> into)
        {
            foreach (var child in world.Children(holder))
            foreach (var source in world.Sources<R>(child))
                into.Add(source);
        }
    }
}
