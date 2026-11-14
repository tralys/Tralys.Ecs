using System.Diagnostics;

namespace Tralys.Ecs;

/// <summary>Per-system run counts, faults, durations, quarantine state and the query chunks each system visited and skipped. Only the tick
/// thread writes, without locks; a reader on any thread retries until it takes a sample no write overlapped.</summary>
public sealed class SystemTiming
{
    struct Slot
    {
        public int Sequence;
        public long Runs;
        public long Faults;
        public long LastTicks;
        public long TotalTicks;
        public long ChunksVisited;
        public long ChunksSkipped;
        public bool Quarantined;
    }

    sealed class Table(Slot[] slots, SystemDescriptor[] systems)
    {
        public readonly Slot[] Slots = slots;
        public readonly SystemDescriptor[] Systems = systems;
    }

    Table table = new([], []);

    /// <summary>Every timed system in schedule order with its sample, all taken from one table, so a replan between reads cannot pair a
    /// system with another's sample.</summary>
    public SystemTimingEntry[] ReadAll()
    {
        var known = Volatile.Read(ref table);
        var entries = new SystemTimingEntry[known.Systems.Length];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = new SystemTimingEntry(known.Systems[i], Read(known.Slots, i));
        return entries;
    }

    static SystemTimingSample Read(Slot[] array, int index)
    {
        ref var slot = ref array[index];
        while (true)
        {
            var before = Volatile.Read(ref slot.Sequence);
            if ((before & 1) == 0)
            {
                var sample = new SystemTimingSample(
                    slot.Runs,
                    slot.Faults,
                    Duration(slot.LastTicks),
                    slot.Runs == 0 ? TimeSpan.Zero : Duration(slot.TotalTicks / slot.Runs),
                    slot.Quarantined,
                    slot.ChunksVisited,
                    slot.ChunksSkipped
                );
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref slot.Sequence) == before)
                    return sample;
            }
            Thread.SpinWait(1);
        }
    }

    /// <summary>The sample of <paramref name="system"/>, or false when the schedule does not hold it.</summary>
    public bool TryRead(SystemId system, out SystemTimingSample sample)
    {
        var known = Volatile.Read(ref table);
        for (var i = 0; i < known.Systems.Length; i++)
        {
            if (known.Systems[i].Id == system)
            {
                sample = Read(known.Slots, i);
                return true;
            }
        }
        sample = default;
        return false;
    }

    internal void Record(
        int index,
        long ticks,
        bool faulted,
        bool quarantined,
        long visited,
        long skipped
    )
    {
        ref var slot = ref table.Slots[index];
        slot.Sequence++;
        Interlocked.MemoryBarrier();
        slot.Runs++;
        if (faulted)
            slot.Faults++;
        slot.LastTicks = ticks;
        slot.TotalTicks += ticks;
        slot.ChunksVisited += visited;
        slot.ChunksSkipped += skipped;
        slot.Quarantined = quarantined;
        Volatile.Write(ref slot.Sequence, slot.Sequence + 1);
    }

    internal void Track(List<SystemSlot> scheduled)
    {
        var current = table;
        var next = new Slot[scheduled.Count];
        var descriptors = new SystemDescriptor[scheduled.Count];
        for (var i = 0; i < scheduled.Count; i++)
        {
            descriptors[i] = scheduled[i].Descriptor;
            var previous = Array.IndexOf(current.Systems, descriptors[i]);
            if (previous >= 0)
                next[i] = current.Slots[previous];
            next[i].Sequence = 0;
        }
        Volatile.Write(ref table, new Table(next, descriptors));
    }

    internal void Reset()
    {
        var current = table;
        Volatile.Write(ref table, new Table(new Slot[current.Slots.Length], current.Systems));
    }

    static TimeSpan Duration(long ticks) =>
        TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
}

/// <summary>One timed system and its sample, as <see cref="SystemTiming.ReadAll"/> pairs them.</summary>
/// <param name="System">The system the sample belongs to.</param>
/// <param name="Sample">Its totals when the table was read.</param>
public readonly record struct SystemTimingEntry(SystemDescriptor System, SystemTimingSample Sample);

/// <summary>One system's totals; <see cref="ChunksVisited"/> and <see cref="ChunksSkipped"/> count the query chunks its runs handed to a
/// body and skipped for having no passing row.</summary>
public readonly record struct SystemTimingSample(
    long Runs,
    long Faults,
    TimeSpan Last,
    TimeSpan Mean,
    bool Quarantined,
    long ChunksVisited,
    long ChunksSkipped
);
