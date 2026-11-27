using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>Receives faults that a run contains instead of propagating. Set it on <see cref="World.Faults"/>; without one, faults
/// propagate.</summary>
public interface IFaultSink
{
    /// <summary>A system or one of its run conditions threw; the tick went on without it.</summary>
    void OnSystemFault(SystemDescriptor system, Exception error);

    /// <summary>A run condition of the <see cref="SystemSetAttribute"/> set <paramref name="set"/> threw; the tick went on without the
    /// set's systems.</summary>
    void OnSetFault(Type set, Exception error);

    /// <summary>A collect-then-act run's act threw for <paramref name="entity"/>; the run went on with the next entity.</summary>
    void OnEntityFault(SystemId system, Entity entity, Exception error);

    /// <summary>The playback at <paramref name="point"/> threw after completing; the tick went on.</summary>
    void OnPlaybackFault(string point, Exception error);
}

/// <summary>What a system body or run condition is given: the world, the run's version and system, the fault sink and the tick.</summary>
public readonly ref struct RunContext
{
    readonly ref readonly byte tick;
    readonly Type? tickType;

    /// <summary>A context on <paramref name="world"/> with no tick, attributed to the system running on it.</summary>
    public RunContext(World world)
    {
        World = world;
        Version = world.Version;
        System = world.CurrentSystem;
        Faults = world.Faults;
    }

    RunContext(World world, ref readonly byte tick, Type tickType, SystemId? system)
        : this(world)
    {
        this.tick = ref tick;
        this.tickType = tickType;
        if (system is { } id)
            System = id;
    }

    /// <summary>A context whose <see cref="Tick{TTick}"/> reads <paramref name="tick"/>. Its <see cref="System"/> is
    /// <paramref name="system"/> when given, such as the system a run condition gates, and otherwise the system running on the
    /// world.</summary>
    public static RunContext WithTick<TTick>(World world, in TTick tick, SystemId? system = null)
        where TTick : struct =>
        new(world, in Unsafe.As<TTick, byte>(ref Unsafe.AsRef(in tick)), typeof(TTick), system);

    /// <summary>The world the run is on.</summary>
    public World World { get; }

    /// <summary>The world version when the context was made.</summary>
    public ulong Version { get; }

    /// <summary>The system the run is attributed to; <c>default</c> when none is running.</summary>
    public SystemId System { get; }

    /// <summary>The world's fault sink when the context was made; null lets faults propagate.</summary>
    public IFaultSink? Faults { get; }

    /// <summary>The tick the schedule runs with.</summary>
    /// <exception cref="InvalidOperationException">The run has no tick, or ticks with another type.</exception>
    public ref readonly TTick Tick<TTick>()
        where TTick : struct
    {
        if (tickType != typeof(TTick))
            ThrowTick(typeof(TTick));
        return ref Unsafe.As<byte, TTick>(ref Unsafe.AsRef(in tick));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void ThrowTick(Type requested) =>
        throw new InvalidOperationException(
            tickType is null
                ? $"This run has no tick; run the system through a schedule or with Run(world, tick) to read {requested.Name}."
                : $"This run ticks with {tickType.Name}, not {requested.Name}."
        );
}

/// <summary>A system. Declare it <c>[System] partial</c>: the generator supplies its descriptor and binds its lookup fields to the world each
/// run. <see cref="OnRun"/> is the body; call the generated <c>RunX</c> of each query from it, in the order they should run.</summary>
public abstract class SystemBase
{
    SystemDescriptor? descriptor;

    /// <summary>The system's descriptor: id, name and set. Created once by <see cref="CreateDescriptor"/>.</summary>
    public SystemDescriptor GetDescriptor() => descriptor ??= CreateDescriptor();

    /// <summary>Supplied by the generator for a <c>[System]</c> class.</summary>
    protected abstract SystemDescriptor CreateDescriptor();

    /// <summary>Opens a run for this system, binds its lookups to <paramref name="world"/> and calls <see cref="OnRun"/>.</summary>
    public void Run(World world)
    {
        using var run = world.BeginSystemRun(GetDescriptor().Id);
        BindLookups(world);
        OnRun(new RunContext(world));
    }

    /// <summary>Runs as <see cref="Run(World)"/>, with <paramref name="tick"/> readable through <see cref="RunContext.Tick{TTick}"/>.</summary>
    public void Run<TTick>(World world, in TTick tick)
        where TTick : struct
    {
        using var run = world.BeginSystemRun(GetDescriptor().Id);
        BindLookups(world);
        OnRun(RunContext.WithTick(world, in tick));
    }

    /// <summary>Supplied by the generator: binds the system's lookup fields to <paramref name="world"/> before every run.</summary>
    protected virtual void BindLookups(World world) { }

    /// <summary>The system body, called once per run; call each generated <c>RunX</c> query runner from it.</summary>
    protected abstract void OnRun(in RunContext context);
}

/// <summary>A system's identity and declared set, generated from its queries and attributes.</summary>
public sealed class SystemDescriptor(
    string name,
    Type? type = null,
    SystemOrder? order = null,
    bool parallel = false
)
{
    /// <summary>A process-wide id issued when the descriptor is created.</summary>
    public SystemId Id { get; } = SystemId.Next();

    /// <summary>The name schedules and diagnostics show; the class name for a generated descriptor.</summary>
    public string Name { get; } = name;

    /// <summary>The system class, or null for a descriptor built by hand.</summary>
    public Type? Type { get; } = type;

    /// <summary>The set the system's attributes put it in; <see cref="SystemOrder.None"/> for none.</summary>
    public SystemOrder Order { get; } = order ?? SystemOrder.None;

    /// <summary>At least one of the system's queries runs its chunks through the world's executor, so a run's duration spans work on other
    /// threads.</summary>
    public bool Parallel { get; } = parallel;

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>Declares a <see cref="SystemBase"/> subclass to the generator.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SystemAttribute : Attribute;
