namespace Tralys.Ecs;

/// <summary>Skips a system after <see cref="FaultPolicy.QuarantineAfter"/> consecutive faults, probing it again every
/// <see cref="FaultPolicy.ProbeSeconds"/>; any run that completes ends the streak.</summary>
internal struct QuarantineState
{
    public int Failures;
    public bool Quarantined;
    double probeAt;

    public readonly bool Skips(double now) => Quarantined && now < probeAt;

    public void Record(bool faulted, double now, in FaultPolicy policy)
    {
        if (!faulted)
        {
            Failures = 0;
            Quarantined = false;
            return;
        }
        Failures++;
        if (policy.QuarantineAfter > 0 && Failures >= policy.QuarantineAfter)
            Quarantined = true;
        if (Quarantined)
            probeAt = now + policy.ProbeSeconds;
    }

    public void Reset() => this = default;
}
