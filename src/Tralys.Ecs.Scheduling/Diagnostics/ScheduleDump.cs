namespace Tralys.Ecs;

public sealed partial class Schedule<TTick>
{
    /// <summary>Writes the sets with their run conditions and the run order with every named playback point.</summary>
    public void Dump(TextWriter writer)
    {
        writer.WriteLine($"Schedule {declaration.Name}");
        writer.WriteLine(
            $"Sets: {string.Join(", ", declaration.Sets.Select((s, i) => s.Name + Gate(i)))}"
        );
        writer.WriteLine("Steps:");
        var number = 0;
        foreach (var step in plan.Steps)
        {
            if (!step.IsSystem)
            {
                writer.WriteLine(
                    $"  -- playback '{step.Name}'{(step.FinalizesDestroys ? " (finalises destroys)" : "")}"
                );
                continue;
            }
            var slot = slots[step.Slot];
            var set =
                slot.SetIndex < declaration.Sets.Count
                    ? declaration.Sets[slot.SetIndex].Name
                    : "default";
            writer.WriteLine($"  {++number, 3}. [{set}] {slot.Descriptor.Name}");
        }
        writer.WriteLine(
            plan.FinalizesAtPoint
                ? $"  -- playback '{EndOfTick}' (trailing)"
                : $"  -- playback '{EndOfTick}' (trailing, finalises destroys)"
        );
    }

    string Gate(int set) =>
        setConditions[set].Length switch
        {
            0 => "",
            1 => " (1 run condition)",
            var count => $" ({count} run conditions)",
        };
}
