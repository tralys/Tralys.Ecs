using System.Diagnostics.CodeAnalysis;

namespace Tralys.Ecs;

/// <summary>One step of a linearised schedule: a system, or a named playback point.</summary>
internal readonly record struct ScheduleStep(int Slot, bool FinalizesDestroys, string? Name)
{
    [MemberNotNullWhen(false, nameof(Name))]
    public bool IsSystem => Name is null;

    public static ScheduleStep System(int slot) => new(slot, false, null);
}

/// <summary>A schedule's systems in run order with its named playback points, and where each set's steps begin and end. Sets run in
/// declaration order, each set's systems in the order they were added, then the systems in no set, in the order they were added.</summary>
internal sealed class SchedulePlan(ScheduleStep[] steps, (int Start, int End)[] setRanges)
{
    public readonly ScheduleStep[] Steps = steps;

    /// <summary>Each declared set's steps, by its index in the declaration, from its first system to past its last; empty when the
    /// set has no systems.</summary>
    public readonly (int Start, int End)[] SetRanges = setRanges;

    /// <summary>The index of the declared set whose first system each step is, or -1.</summary>
    public readonly int[] SetStartingAt = StartsOf(steps.Length, setRanges);
    public readonly bool FinalizesAtPoint = steps.Any(s => s.FinalizesDestroys);

    public static SchedulePlan Build(ScheduleDeclaration declaration, List<SystemSlot> slots)
    {
        var problems = new List<string>();
        var sets = declaration.Sets.Count;
        var setIndex = new Dictionary<Type, int>();
        for (var set = 0; set < sets; set++)
        {
            if (!setIndex.TryAdd(declaration.Sets[set], set))
                problems.Add($"{declaration.Name} lists set {declaration.Sets[set].Name} twice.");
        }
        var setOf = new int[slots.Count];
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            setOf[i] = sets;
            if (slot.ConflictingDeclaredSet is { } declared)
                problems.Add(
                    $"{slot.Descriptor.Name} is declared in set {declared.Name} and configured into another."
                );
            if (slot.Set is not { } set)
                continue;
            if (setIndex.TryGetValue(set, out var index))
                setOf[i] = index;
            else
                problems.Add(
                    $"{slot.Descriptor.Name} is in set {set.Name}, which {declaration.Name} does not list."
                );
        }

        var bounds = new int[sets + 2];
        foreach (var set in setOf)
            bounds[set + 1]++;
        for (var set = 1; set < bounds.Length; set++)
            bounds[set] += bounds[set - 1];
        var order = new int[slots.Count];
        var position = new int[slots.Count];
        var next = bounds[..^1];
        for (var i = 0; i < slots.Count; i++)
        {
            position[i] = next[setOf[i]]++;
            order[position[i]] = i;
        }

        var points = new List<(int Gap, PlaybackPointDeclaration Point)>();
        var names = new HashSet<string>();
        var finalizing = 0;
        foreach (var point in declaration.Points)
        {
            if (!names.Add(point.Name))
                problems.Add($"{declaration.Name} declares playback point {point.Name} twice.");
            if (point.FinalizesDestroys && ++finalizing == 2)
                problems.Add(
                    $"{declaration.Name} declares more than one playback point that finalises destroys."
                );
            int? after = null;
            int? before = null;
            foreach (var target in point.After)
            {
                if (Gap(target, true) is { } gap)
                    after = Math.Max(after ?? 0, gap);
            }
            foreach (var target in point.Before)
            {
                if (Gap(target, false) is { } gap)
                    before = Math.Min(before ?? int.MaxValue, gap);
            }
            if (after > before)
                problems.Add(
                    $"Playback point {point.Name} cannot run both after {string.Join(", ", point.After.Select(t => t.Name))} and before {string.Join(", ", point.Before.Select(t => t.Name))} in {declaration.Name}."
                );
            points.Add((after ?? before ?? 0, point));

            int? Gap(Type target, bool following)
            {
                if (setIndex.TryGetValue(target, out var set))
                    return following ? bounds[set + 1] : bounds[set];
                var system = slots.FindIndex(s => s.Type == target);
                if (system >= 0)
                    return position[system] + (following ? 1 : 0);
                problems.Add(
                    $"Playback point {point.Name} runs {(following ? "after" : "before")} {target.Name}, which is neither a set nor a system of {declaration.Name}."
                );
                return null;
            }
        }
        if (problems.Count > 0)
            throw new ScheduleBuildException(declaration.Name, problems);

        for (var i = 0; i < slots.Count; i++)
        {
            slots[i].Index = i;
            slots[i].SetIndex = setOf[i];
        }
        var steps = new List<ScheduleStep>(slots.Count + points.Count);
        var stepOf = new int[slots.Count];
        for (var gap = 0; gap <= slots.Count; gap++)
        {
            foreach (var (at, point) in points)
            {
                if (at == gap)
                    steps.Add(new ScheduleStep(-1, point.FinalizesDestroys, point.Name));
            }
            if (gap == slots.Count)
                break;
            stepOf[gap] = steps.Count;
            steps.Add(ScheduleStep.System(order[gap]));
        }
        var ranges = new (int Start, int End)[sets];
        for (var set = 0; set < sets; set++)
        {
            if (bounds[set + 1] > bounds[set])
                ranges[set] = (stepOf[bounds[set]], stepOf[bounds[set + 1] - 1] + 1);
        }
        return new SchedulePlan([.. steps], ranges);
    }

    static int[] StartsOf(int steps, (int Start, int End)[] ranges)
    {
        var starts = new int[steps];
        Array.Fill(starts, -1);
        for (var set = 0; set < ranges.Length; set++)
        {
            if (ranges[set].End > ranges[set].Start)
                starts[ranges[set].Start] = set;
        }
        return starts;
    }
}

/// <summary>Thrown by <see cref="ScheduleBuilder{TTick}.Build"/> and by schedule changes for an unknown or repeated set, a playback point
/// naming something outside the schedule or placed both after and before what runs in the other order, and a declared system left
/// out.</summary>
public sealed class ScheduleBuildException(string schedule, IReadOnlyList<string> problems)
    : InvalidOperationException($"{schedule} cannot be built:\n  " + string.Join("\n  ", problems))
{
    /// <summary>One message per problem the build found.</summary>
    public IReadOnlyList<string> Problems { get; } = problems;
}
