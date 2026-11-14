namespace Tralys.Ecs;

/// <summary>Wraps a schedule's runs, for profiling and tracing. The schedule hands each set, system run and playback to it as work it runs
/// once, inside whatever scope it opens, and tells it each system it skipped. Give it to
/// <see cref="ScheduleBuilder{TTick}.Observe"/>.</summary>
public interface IScheduleObserver
{
    /// <summary>Runs the steps of <paramref name="set"/>, one of the declaration's sets; its run conditions are checked, and the system
    /// runs, skips and playbacks between its first and last system reach this observer, from inside <see cref="ScheduledWork.Run"/>.</summary>
    void RunSet(Type set, ScheduledWork work);

    /// <summary>Runs <paramref name="system"/>; a fault the world's sink contains does not escape <see cref="ScheduledWork.Run"/>.</summary>
    void RunSystem(SystemDescriptor system, ScheduledWork work);

    /// <summary>Plays back at <paramref name="point"/>: a declared point's name, <c>After{System}</c> with the class name of a system whose
    /// run left commands or observer events to play back, or <c>EndOfTick</c>, <c>EndOfSet</c> or <c>EndOfSystem</c> for the playback that
    /// closes a run. A playback fault the world's sink contains does not escape <see cref="ScheduledWork.Run"/>.</summary>
    void RunPlayback(string point, ScheduledWork work);

    /// <summary>The schedule skipped <paramref name="system"/> this tick for <paramref name="reason"/>.</summary>
    void OnSystemSkipped(SystemDescriptor system, SystemSkip reason);
}

/// <summary>Why a schedule skipped a system on a tick, in the order it checks them after its set's run conditions.</summary>
public enum SystemSkip
{
    /// <summary>The system is quarantined and its next probe is not due.</summary>
    Quarantined,

    /// <summary>A run condition of the system or of its set was false, or threw into the world's fault sink.</summary>
    Condition,

    /// <summary>The system's cadence interval has not elapsed.</summary>
    Cadence,
}

/// <summary>The set, system run or playback an <see cref="IScheduleObserver"/> wraps.</summary>
public readonly ref struct ScheduledWork
{
    readonly IScheduleSteps steps;
    readonly ref readonly byte tick;
    readonly ref bool ran;
    readonly ScheduledWorkKind kind;
    readonly int first;
    readonly int last;
    readonly string? point;

    internal ScheduledWork(
        IScheduleSteps steps,
        ref readonly byte tick,
        ref bool ran,
        ScheduledWorkKind kind,
        int first,
        int last,
        string? point
    )
    {
        this.steps = steps;
        this.tick = ref tick;
        this.ran = ref ran;
        this.kind = kind;
        this.first = first;
        this.last = last;
        this.point = point;
    }

    /// <summary>Runs the work. The observer must call it exactly once: the schedule throws <see cref="InvalidOperationException"/> when
    /// the observer returns without running it.</summary>
    /// <exception cref="InvalidOperationException">The work already ran, or no schedule handed it out.</exception>
    public void Run()
    {
        if (steps is null)
            throw new InvalidOperationException("Only a schedule hands out work to run.");
        if (ran)
            throw new InvalidOperationException(
                "This work already ran; an observer runs each set, system and playback once."
            );
        ran = true;
        steps.Run(in tick, kind, first, last, point);
    }
}

internal enum ScheduledWorkKind
{
    Set,
    System,
    Playback,
}

internal interface IScheduleSteps
{
    void Run(ref readonly byte tick, ScheduledWorkKind kind, int first, int last, string? point);
}
