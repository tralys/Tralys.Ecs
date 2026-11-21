namespace Tralys.Ecs;

/// <summary>One system of a schedule with its configuration and the state its policies keep between ticks.</summary>
internal sealed class SystemSlot(SystemBase system, Type type, SystemDescriptor descriptor)
{
    public readonly SystemBase System = system;
    public readonly Type Type = type;
    public readonly SystemDescriptor Descriptor = descriptor;
    public readonly string PlaybackPoint = "After" + type.Name;
    public Type? Set = descriptor.Order.Set;
    public RunCondition[] Conditions = [];
    public CadenceState Cadence;
    public QuarantineState Quarantine;
    public int Index;
    public int SetIndex;
    public Type? ConflictingDeclaredSet;
}
