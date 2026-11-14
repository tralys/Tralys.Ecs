namespace Tralys.Ecs;

/// <summary>The app's clock for one tick. Cadence and quarantine probes read <see cref="Seconds"/>.</summary>
public interface ITickContext
{
    /// <summary>The tick's time in seconds from any fixed origin; cadence intervals and quarantine probe delays are measured on it.</summary>
    double Seconds { get; }
}
