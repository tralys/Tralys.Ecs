namespace Tralys.Ecs;

/// <summary>Steps several worlds tick by tick in lockstep, each under the schedule built for it, and resets them all to a baseline between
/// tests.</summary>
public sealed class WorldHarness<TTick>
    where TTick : struct, ITickContext
{
    readonly Func<long, TTick> tickAt;
    readonly List<World> worlds = [];
    readonly List<Schedule<TTick>> schedules = [];
    WorldSnapshot[]? baselines;
    long baselineTick;

    /// <param name="tickAt">The tick context of tick number n, counted from 0.</param>
    public WorldHarness(Func<long, TTick> tickAt)
    {
        ArgumentNullException.ThrowIfNull(tickAt);
        this.tickAt = tickAt;
    }

    /// <summary>The number of the next tick: ticks stepped so far, set back to the baseline's count by <see cref="Reset"/>.</summary>
    public long Tick { get; private set; }

    /// <summary>The added worlds, in the order each step runs them.</summary>
    public IReadOnlyList<World> Worlds => worlds;

    /// <summary>Adds the world <paramref name="schedule"/> was built for; each step runs the worlds in the order they were added.</summary>
    public WorldHarness<TTick> Add(Schedule<TTick> schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var world = schedule.World;
        if (worlds.Contains(world))
            throw new ArgumentException("The world is already in the harness.", nameof(schedule));
        worlds.Add(world);
        schedules.Add(schedule);
        baselines = null;
        return this;
    }

    /// <summary>Runs <paramref name="ticks"/> ticks; in each, every world runs one tick of its schedule. A schedule that throws leaves the
    /// tick half run, with <see cref="Tick"/> not advanced; <see cref="Reset"/> recovers every world.</summary>
    public void Step(int ticks = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        for (var t = 0; t < ticks; t++)
        {
            var context = tickAt(Tick);
            for (var i = 0; i < worlds.Count; i++)
                schedules[i].Run(context);
            Tick++;
        }
    }

    /// <summary>Steps until <paramref name="done"/> holds, checking before each tick, and returns how many ticks ran.</summary>
    /// <exception cref="TimeoutException"><paramref name="done"/> still failed after <paramref name="maxTicks"/> ticks.</exception>
    public int RunUntil(Func<bool> done, int maxTicks)
    {
        ArgumentNullException.ThrowIfNull(done);
        for (var ran = 0; ran <= maxTicks; ran++)
        {
            if (done())
                return ran;
            if (ran < maxTicks)
                Step();
        }
        throw new TimeoutException($"The condition still failed after {maxTicks} ticks.");
    }

    /// <summary>Steps until a whole tick makes no structural change in any world and leaves <paramref name="progress"/>, when given,
    /// unchanged; returns how many ticks ran, the quiet one included. Value writes do not count as change, replacing a
    /// <see cref="ReplaceOnlyAttribute"/> component included.</summary>
    /// <exception cref="TimeoutException">A world still changed after <paramref name="maxTicks"/> ticks.</exception>
    public int RunUntilQuiescent(int maxTicks, Func<long>? progress = null)
    {
        var measured = progress?.Invoke() ?? 0;
        var counted = StructuralChanges();
        for (var ran = 1; ran <= maxTicks; ran++)
        {
            Step();
            var now = progress?.Invoke() ?? 0;
            var changes = StructuralChanges();
            if (changes == counted && now == measured)
                return ran;
            measured = now;
            counted = changes;
        }
        throw new TimeoutException($"The worlds still changed after {maxTicks} ticks.");
    }

    long StructuralChanges()
    {
        var total = 0L;
        foreach (var world in worlds)
            total += world.Introspection.StructuralChanges;
        return total;
    }

    /// <summary>Snapshots every world as the baseline <see cref="Reset"/> returns to, and remembers the tick count.</summary>
    public void CaptureBaseline()
    {
        var taken = new WorldSnapshot[worlds.Count];
        for (var i = 0; i < worlds.Count; i++)
            taken[i] = worlds[i].Snapshot();
        baselines = taken;
        baselineTick = Tick;
    }

    /// <summary>Resets every world to its baseline, clears each schedule's quarantine, cadence and timing, and rewinds the tick count, so
    /// the next test starts where the baseline was taken.</summary>
    public void Reset()
    {
        if (baselines is null)
            throw new InvalidOperationException(
                "No baseline: call CaptureBaseline after the last Add."
            );
        for (var i = 0; i < worlds.Count; i++)
        {
            worlds[i].Reset(baselines[i]);
            schedules[i].ResetState();
        }
        Tick = baselineTick;
    }
}
