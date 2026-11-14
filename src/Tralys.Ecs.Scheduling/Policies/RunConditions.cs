namespace Tralys.Ecs;

/// <summary>Whether a system, or every system of a set, runs this tick. Evaluated before each run, so it must not allocate.</summary>
public delegate bool RunCondition(in RunContext context);

internal static class RunConditions
{
    public static bool Pass<TTick>(
        RunCondition[] conditions,
        World world,
        in TTick tick,
        SystemId? system
    )
        where TTick : struct
    {
        if (conditions.Length == 0)
            return true;
        var context = RunContext.WithTick(world, in tick, system);
        foreach (var condition in conditions)
        {
            if (!condition(context))
                return false;
        }
        return true;
    }
}
