namespace Tralys.Ecs.Examples;

public static class WorldBasics
{
    public static World CreateWorld() => World.Create(Registry.Build(EcsModule.All));

    public static Entity Spawn(World world, int code, float x, float y)
    {
        var entity = world.Create();
        world.Set(entity, new Position { X = x, Y = y });
        world.Set(entity, new Velocity { X = 1 });
        world.Set(entity, new Unit { Code = code, Health = 10 });
        world.Add<Visible>(entity);
        return entity;
    }

    public static float XOf(World world, Entity entity) =>
        world.TryGet<Position>(entity, out var position) ? position.X : 0;

    public static void Nudge(World world, Entity entity)
    {
        using var edit = world.Edit<Position>(entity);
        edit.Value.X += 1;
    }

    public static void Damage(World world, Entity entity, int amount)
    {
        using var edit = world.EditOrAdd(entity, new Unit { Health = 10 });
        edit.Value.Health -= amount;
    }

    public static void Freeze(World world, Entity entity)
    {
        world.Add<Frozen>(entity);
        world.SetEnabled<Visible>(entity, false);
        world.Add<Selected>(entity);
    }

    public static bool Thaw(World world, Entity entity)
    {
        world.Add<Visible>(entity);
        world.Remove<Selected>(entity);
        return world.Remove<Frozen>(entity);
    }

    public static Entity FindByCode(World world, int code) =>
        world.UnitByCode().TryGet(code, out var entity) ? entity : default(Entity);

    public static void AdvanceClock(World world, double seconds)
    {
        var now = world.TryGetSingleton<Clock>(out var clock) ? clock.Seconds : 0;
        world.SetSingleton(new Clock { Seconds = now + seconds });
    }
}
