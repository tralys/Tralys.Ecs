namespace Tralys.Ecs;

/// <summary>Composes a schedule from its declaration and the systems added at run time: sets run in declaration order, each set's systems
/// in the order they were added, then the systems in no set. Once it has built, every method throws
/// <see cref="InvalidOperationException"/>.</summary>
public sealed class ScheduleBuilder<TTick>(ScheduleDeclaration declaration)
    where TTick : struct, ITickContext
{
    readonly List<SystemSlot> slots = [];
    readonly Dictionary<Type, List<RunCondition>> setConditions = [];
    FaultPolicy policy;
    IScheduleObserver? observer;
    readonly BuildState state = new();

    /// <summary>Adds <paramref name="system"/> to the set its attributes or <paramref name="configure"/> name, after the systems already
    /// added there. Adding a system type twice throws.</summary>
    public ScheduleBuilder<TTick> Add<TSystem>(
        TSystem system,
        Action<SystemConfig>? configure = null
    )
        where TSystem : SystemBase
    {
        ArgumentNullException.ThrowIfNull(system);
        state.CheckOpen();
        var slot = Slot(slots, system);
        slots.Add(slot);
        configure?.Invoke(new SystemConfig(slot));
        return this;
    }

    /// <summary>Configures <typeparamref name="TSet"/>, one of the declaration's sets, for every system in it, those added after the build
    /// included. Throws <see cref="ArgumentException"/> when the declaration does not list the set.</summary>
    public ScheduleBuilder<TTick> ConfigureSet<TSet>(Action<SetConfig> configure)
        where TSet : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        state.CheckOpen();
        if (declaration.IndexOf(typeof(TSet)) < 0)
            throw new ArgumentException($"{declaration.Name} has no set {typeof(TSet).Name}.");
        if (!setConditions.TryGetValue(typeof(TSet), out var conditions))
            setConditions[typeof(TSet)] = conditions = [];
        configure(new SetConfig(conditions, state));
        return this;
    }

    /// <summary>Skips a system whose faults <see cref="World.Faults"/> contained <paramref name="after"/> times in a row, then probes it
    /// every <paramref name="probeEvery"/>. Without a sink on the world, faults propagate and nothing is quarantined.</summary>
    public ScheduleBuilder<TTick> Quarantine(int after, TimeSpan probeEvery)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        state.CheckOpen();
        policy = new FaultPolicy(after, probeEvery.TotalSeconds);
        return this;
    }

    /// <summary>Hands every set, system run, skip and playback of the schedule to <paramref name="observer"/>, replacing an observer
    /// given before.</summary>
    public ScheduleBuilder<TTick> Observe(IScheduleObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        state.CheckOpen();
        this.observer = observer;
        return this;
    }

    /// <summary>Builds the schedule for <paramref name="world"/>. A builder builds one schedule: its world, systems and their cadence,
    /// quarantine and timing state belong to it.</summary>
    /// <exception cref="ScheduleBuildException">An unknown or repeated set, a misplaced playback point, or a system of the declaration's
    /// sets that was never added.</exception>
    /// <exception cref="InvalidOperationException">This builder already built a schedule.</exception>
    public Schedule<TTick> Build(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        state.CheckOpen();
        var missing = new List<string>();
        foreach (var type in declaration.DeclaredOrder)
        {
            if (!slots.Exists(s => s.Descriptor.Type == type))
                missing.Add(
                    $"{type.Name} is in a set of {declaration.Name} but was not added; add it, or move it out of the schedule's sets."
                );
        }
        if (missing.Count > 0)
            throw new ScheduleBuildException(declaration.Name, missing);
        var scheduled = new List<SystemSlot>(slots);
        var plan = SchedulePlan.Build(declaration, scheduled);
        var gates = new RunCondition[declaration.Sets.Count][];
        for (var set = 0; set < gates.Length; set++)
            gates[set] = setConditions.TryGetValue(declaration.Sets[set], out var conditions)
                ? [.. conditions]
                : [];
        var schedule = new Schedule<TTick>(
            world,
            declaration,
            scheduled,
            plan,
            gates,
            policy,
            observer
        );
        state.Built = true;
        return schedule;
    }

    internal static SystemSlot Slot(List<SystemSlot> slots, SystemBase system)
    {
        var type = system.GetType();
        if (slots.Exists(s => s.Type == type))
            throw new ArgumentException($"{type.Name} is already in the schedule.", nameof(system));
        return new SystemSlot(system, type, system.GetDescriptor());
    }
}

/// <summary>Run-time configuration of one added system, on top of its attributes.</summary>
public sealed class SystemConfig
{
    readonly SystemSlot slot;

    internal SystemConfig(SystemSlot slot) => this.slot = slot;

    /// <summary>Runs the system only on ticks where every condition holds. Conditions run before each run and must not allocate; a static
    /// lambda does not.</summary>
    public SystemConfig RunIf(RunCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        slot.Conditions = [.. slot.Conditions, condition];
        return this;
    }

    /// <summary>Runs the system at most once per <paramref name="interval"/> of tick time. A late tick runs it once, without catching up.</summary>
    public SystemConfig Cadence(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        slot.Cadence.Interval = interval.TotalSeconds;
        return this;
    }

    /// <summary>Places the system in <typeparamref name="TSet"/>; the build fails if its attributes name another set or the schedule does
    /// not list this one.</summary>
    public SystemConfig InSet<TSet>()
        where TSet : class
    {
        if (slot.Descriptor.Order.Set is { } declared && declared != typeof(TSet))
            slot.ConfigSetConflict = true;
        slot.Set = typeof(TSet);
        return this;
    }
}

/// <summary>Run-time configuration of one set of a schedule, applied to every system in it; it throws
/// <see cref="InvalidOperationException"/> once the schedule is built.</summary>
public sealed class SetConfig
{
    readonly List<RunCondition> conditions;
    readonly BuildState state;

    internal SetConfig(List<RunCondition> conditions, BuildState state)
    {
        this.conditions = conditions;
        this.state = state;
    }

    /// <summary>Runs the set's systems only on ticks where every condition holds, checked once per run of the set before each system's
    /// own checks, with no system in its context; a false one skips each system as <see cref="SystemSkip.Condition"/>, never a playback
    /// point. It must not allocate; a static lambda does not.</summary>
    public SetConfig RunIf(RunCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        state.CheckOpen();
        conditions.Add(condition);
        return this;
    }
}

internal sealed class BuildState
{
    public bool Built;

    public void CheckOpen()
    {
        if (Built)
            throw new InvalidOperationException(
                "This builder already built its schedule; add systems to the schedule itself, and build each schedule from its own builder."
            );
    }
}
