namespace Tralys.Ecs;

/// <summary>When a schedule quarantines a system whose faults <see cref="World.Faults"/> contained.</summary>
internal readonly record struct FaultPolicy(int QuarantineAfter, double ProbeSeconds);
