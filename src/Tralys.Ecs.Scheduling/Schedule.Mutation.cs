namespace Tralys.Ecs;

public sealed partial class Schedule<TTick>
{
    /// <summary>Adds a system between ticks, after the systems already in its set, and replans.</summary>
    /// <exception cref="ScheduleBuildException">The system's set is not the schedule's, or its attributes and configuration name two;
    /// the schedule is left as it was.</exception>
    public void Add<TSystem>(TSystem system, Action<SystemConfig>? configure = null)
        where TSystem : SystemBase
    {
        CheckIdle();
        ArgumentNullException.ThrowIfNull(system);
        var slot = ScheduleBuilder<TTick>.Slot(slots, system);
        configure?.Invoke(new SystemConfig(slot));
        Replan([.. slots, slot]);
    }

    /// <summary>Removes a system between ticks and replans. Returns false when the schedule does not hold it.</summary>
    /// <exception cref="ScheduleBuildException">A playback point is placed against the system; the schedule is left as it
    /// was.</exception>
    public bool Remove<TSystem>()
        where TSystem : SystemBase
    {
        CheckIdle();
        var at = slots.FindIndex(s => s.Type == typeof(TSystem));
        if (at < 0)
            return false;
        var next = new List<SystemSlot>(slots);
        next.RemoveAt(at);
        Replan(next);
        return true;
    }

    void Replan(List<SystemSlot> next)
    {
        plan = SchedulePlan.Build(declaration, next);
        slots.Clear();
        slots.AddRange(next);
        Timing.Track(slots);
    }
}
