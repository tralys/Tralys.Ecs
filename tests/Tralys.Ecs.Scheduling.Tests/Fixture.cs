namespace Tralys.Ecs.Scheduling.Tests;

[Component]
public partial struct Score
{
    public int Value;
}

[Component]
public partial struct Spawned
{
    public int Generation;
}

[Component]
public partial struct Heat
{
    public int Value;
}

[SystemSet]
public sealed class Early;

[SystemSet]
public sealed class Late;

public readonly record struct Tick(double Seconds) : ITickContext;

static class Worlds
{
    public static readonly Registry Registry = Registry.Build(EcsModule.Module);

    public static World Create(IChunkExecutor? executor = null) =>
        World.Create(Registry, new WorldOptions { Executor = executor, CheckThread = true });
}

/// <summary>Records each system and playback fault, in order, and apart from them each entity fault.</summary>
sealed class FaultLog : IFaultSink
{
    public readonly List<Exception> Faults = [];
    public readonly List<Type> SetFaults = [];
    public readonly List<(SystemId System, Entity Entity)> EntityFaults = [];

    public void OnSystemFault(SystemDescriptor system, Exception error) => Faults.Add(error);

    public void OnSetFault(Type set, Exception error) => SetFaults.Add(set);

    public void OnEntityFault(SystemId system, Entity entity, Exception error) =>
        EntityFaults.Add((system, entity));

    public void OnPlaybackFault(string point, Exception error) => Faults.Add(error);
}
