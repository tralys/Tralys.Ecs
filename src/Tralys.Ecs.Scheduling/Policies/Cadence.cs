namespace Tralys.Ecs;

/// <summary>Runs a system at most once per interval on the tick clock. A late tick runs it once and keeps the phase, without catching up
/// the intervals it missed.</summary>
internal struct CadenceState
{
    public double Interval;
    double next;
    bool started;

    public bool Due(double now)
    {
        if (Interval <= 0)
            return true;
        if (!started)
        {
            started = true;
            next = now + Interval;
            return true;
        }
        if (now < next)
            return false;
        next += (Math.Floor((now - next) / Interval) + 1) * Interval;
        return true;
    }

    public void Reset()
    {
        started = false;
        next = 0;
    }
}
