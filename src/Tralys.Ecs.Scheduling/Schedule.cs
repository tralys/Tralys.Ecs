using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>A linearised schedule bound to the world it was built for. Run it from that world's owner thread.</summary>
public sealed partial class Schedule<TTick> : IScheduleSteps
    where TTick : struct, ITickContext
{
    const string EndOfTick = "EndOfTick";
    const string EndOfSet = "EndOfSet";
    const string EndOfSystem = "EndOfSystem";

    readonly World world;
    readonly ScheduleDeclaration declaration;
    readonly List<SystemSlot> slots;
    readonly RunCondition[][] setConditions;
    readonly FaultPolicy policy;
    readonly IScheduleObserver? observer;
    SchedulePlan plan;
    bool running;

    internal Schedule(
        World world,
        ScheduleDeclaration declaration,
        List<SystemSlot> slots,
        SchedulePlan plan,
        RunCondition[][] setConditions,
        FaultPolicy policy,
        IScheduleObserver? observer
    )
    {
        this.world = world;
        this.declaration = declaration;
        this.slots = slots;
        this.plan = plan;
        this.setConditions = setConditions;
        this.policy = policy;
        this.observer = observer;
        Timing.Track(slots);
    }

    /// <summary>The declaration the schedule was built from.</summary>
    public ScheduleDeclaration Declaration => declaration;

    /// <summary>The world the schedule was built for and runs on.</summary>
    public World World => world;

    /// <summary>Per-system run counts, faults, durations and quarantine state, readable from any thread.</summary>
    public SystemTiming Timing { get; } = new();

    /// <summary>Runs every system in order with its playback points, playing back after each system that leaves work for a playback, then
    /// plays back, finalising pending destroys unless a point declared with <see cref="PlaybackPointAttribute.FinalizesDestroys"/> does so
    /// instead.</summary>
    public void Run(in TTick tick)
    {
        Enter();
        try
        {
            RunSteps(tick);
            Play(tick, EndOfTick, !plan.FinalizesAtPoint);
        }
        finally
        {
            running = false;
        }
    }

    /// <summary>Runs only the members of <typeparamref name="TSet"/>, under the set's run conditions, with the playback points between
    /// them, then plays back. Pending destroys are finalised as <see cref="Run"/> would, at the end unless a finalising point does it,
    /// unless <paramref name="finalizeDestroys"/> says otherwise.</summary>
    public void RunSet<TSet>(in TTick tick, bool? finalizeDestroys = null)
        where TSet : class
    {
        var set = declaration.IndexOf(typeof(TSet));
        if (set < 0)
            throw new ArgumentException($"{declaration.Name} has no set {typeof(TSet).Name}.");
        Enter();
        try
        {
            var (start, end) = plan.SetRanges[set];
            if (end > start)
                RunSet(tick, set, start, end);
            Play(tick, EndOfSet, finalizeDestroys ?? !plan.FinalizesAtPoint);
        }
        finally
        {
            running = false;
        }
    }

    /// <summary>Runs one system under its set's and its own run conditions, cadence and quarantine, then plays back, finalising pending
    /// destroys unless <paramref name="finalizeDestroys"/> is false.</summary>
    public void RunSystem<TSystem>(in TTick tick, bool finalizeDestroys = true)
        where TSystem : SystemBase
    {
        var slot =
            slots.Find(s => s.Type == typeof(TSystem))
            ?? throw new ArgumentException(
                $"{declaration.Name} has no system {typeof(TSystem).Name}."
            );
        Enter();
        try
        {
            if (SetPasses(slot.SetIndex, tick))
                RunSlot(tick, slot);
            else
                Skipped(slot, SystemSkip.Condition);
            Play(tick, EndOfSystem, finalizeDestroys);
        }
        finally
        {
            running = false;
        }
    }

    /// <summary>Clears every system's quarantine, fault streak, cadence phase and timing, so the schedule can serve the next test.</summary>
    public void ResetState()
    {
        CheckIdle();
        foreach (var slot in slots)
        {
            slot.Quarantine.Reset();
            slot.Cadence.Reset();
        }
        Timing.Reset();
    }

    void Enter()
    {
        CheckIdle();
        running = true;
    }

    void CheckIdle()
    {
        if (running)
            throw new InvalidOperationException(
                $"{declaration.Name} is running; change or reset it between ticks."
            );
    }

    void RunSteps(in TTick tick)
    {
        for (var i = 0; i < plan.Steps.Length; i++)
        {
            if (plan.SetStartingAt[i] is var set and >= 0)
            {
                var end = plan.SetRanges[set].End;
                RunSet(tick, set, i, end);
                i = end - 1;
            }
            else
                RunStep(tick, i);
        }
    }

    void RunSet(in TTick tick, int set, int start, int end)
    {
        if (observer is null)
        {
            RunSetSteps(tick, start, end);
            return;
        }
        var type = declaration.Sets[set];
        var ran = false;
        observer.RunSet(type, Work(tick, ref ran, ScheduledWorkKind.Set, start, end, null));
        CheckRan(observer, ran, type.Name);
    }

    void RunSetSteps(in TTick tick, int start, int end)
    {
        if (SetPasses(plan.SetStartingAt[start], tick))
        {
            for (var i = start; i < end; i++)
                RunStep(tick, i);
            return;
        }
        // Points are not the set's to skip: they also apply and finalise what systems outside it recorded.
        for (var i = start; i < end; i++)
        {
            ref readonly var step = ref plan.Steps[i];
            if (step.IsSystem)
                Skipped(slots[step.Slot], SystemSkip.Condition);
            else
                Play(tick, step.Name, step.FinalizesDestroys);
        }
    }

    void RunStep(in TTick tick, int index)
    {
        ref readonly var step = ref plan.Steps[index];
        if (!step.IsSystem)
            Play(tick, step.Name, step.FinalizesDestroys);
        else if (RunSlot(tick, slots[step.Slot]) && world.PlaybackOwed)
            Play(tick, slots[step.Slot].PlaybackPoint, false);
    }

    void Play(in TTick tick, string point, bool finalizeDestroys)
    {
        if (observer is null)
        {
            Playback(point, finalizeDestroys);
            return;
        }
        var ran = false;
        observer.RunPlayback(
            point,
            Work(tick, ref ran, ScheduledWorkKind.Playback, finalizeDestroys ? 1 : 0, 0, point)
        );
        CheckRan(observer, ran, point);
    }

    // With a sink, a failing playback is reported and the tick goes on; without one it propagates.
    void Playback(string point, bool finalizeDestroys)
    {
        if (world.Faults is not { } sink)
        {
            world.Playback(finalizeDestroys);
            return;
        }
        try
        {
            world.Playback(finalizeDestroys);
        }
        catch (Exception error)
        {
            sink.OnPlaybackFault(point, error);
        }
    }

    bool RunSlot(in TTick tick, SystemSlot slot)
    {
        var now = tick.Seconds;
        if (slot.Quarantine.Skips(now))
            Skipped(slot, SystemSkip.Quarantined);
        else if (!Passes(slot, in tick))
            Skipped(slot, SystemSkip.Condition);
        else if (!slot.Cadence.Due(now))
            Skipped(slot, SystemSkip.Cadence);
        else
        {
            if (observer is null)
                Execute(tick, slot);
            else
            {
                var ran = false;
                observer.RunSystem(
                    slot.Descriptor,
                    Work(tick, ref ran, ScheduledWorkKind.System, slot.Index, 0, null)
                );
                CheckRan(observer, ran, slot.Descriptor.Name);
            }
            return true;
        }
        return false;
    }

    void Skipped(SystemSlot slot, SystemSkip reason) =>
        observer?.OnSystemSkipped(slot.Descriptor, reason);

    void Execute(in TTick tick, SystemSlot slot)
    {
        var now = tick.Seconds;
        var faulted = false;
        var introspection = world.Introspection;
        var visited = introspection.ChunksVisited;
        var skipped = introspection.ChunksSkipped;
        var start = Stopwatch.GetTimestamp();
        if (world.Faults is { } sink)
        {
            try
            {
                slot.System.Run(world, in tick);
            }
            catch (Exception error)
            {
                faulted = true;
                sink.OnSystemFault(slot.Descriptor, error);
            }
        }
        else
            slot.System.Run(world, in tick);
        var elapsed = Stopwatch.GetTimestamp() - start;
        slot.Quarantine.Record(faulted, now, policy);
        Timing.Record(
            slot.Index,
            elapsed,
            faulted,
            slot.Quarantine.Quarantined,
            introspection.ChunksVisited - visited,
            introspection.ChunksSkipped - skipped
        );
    }

    bool SetPasses(int set, in TTick tick)
    {
        if (set >= setConditions.Length || setConditions[set].Length == 0)
            return true;
        if (world.Faults is not { } sink)
            return RunConditions.Pass(setConditions[set], world, in tick, default(SystemId));
        try
        {
            return RunConditions.Pass(setConditions[set], world, in tick, default(SystemId));
        }
        catch (Exception error)
        {
            sink.OnSetFault(declaration.Sets[set], error);
            return false;
        }
    }

    bool Passes(SystemSlot slot, in TTick tick)
    {
        if (world.Faults is not { } sink)
            return RunConditions.Pass(slot.Conditions, world, in tick, slot.Descriptor.Id);
        try
        {
            return RunConditions.Pass(slot.Conditions, world, in tick, slot.Descriptor.Id);
        }
        catch (Exception error)
        {
            sink.OnSystemFault(slot.Descriptor, error);
            return false;
        }
    }

    ScheduledWork Work(
        in TTick tick,
        ref bool ran,
        ScheduledWorkKind kind,
        int first,
        int last,
        string? point
    ) =>
        new(
            this,
            in Unsafe.As<TTick, byte>(ref Unsafe.AsRef(in tick)),
            ref ran,
            kind,
            first,
            last,
            point
        );

    void IScheduleSteps.Run(
        ref readonly byte tick,
        ScheduledWorkKind kind,
        int first,
        int last,
        string? point
    )
    {
        ref readonly var typed = ref Unsafe.As<byte, TTick>(ref Unsafe.AsRef(in tick));
        switch (kind)
        {
            case ScheduledWorkKind.Set:
                RunSetSteps(typed, first, last);
                break;
            case ScheduledWorkKind.System:
                Execute(typed, slots[first]);
                break;
            case ScheduledWorkKind.Playback when point is not null:
                Playback(point, first != 0);
                break;
        }
    }

    static void CheckRan(IScheduleObserver observer, bool ran, string work)
    {
        if (!ran)
            throw new InvalidOperationException(
                $"{observer.GetType().Name} returned without running {work}; an observer runs every work it is handed once."
            );
    }
}
