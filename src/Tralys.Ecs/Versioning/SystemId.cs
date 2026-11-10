namespace Tralys.Ecs;

/// <summary>Names a system for run attribution and per-system query watermarks; <c>default</c> names none. Only <see cref="Next"/>
/// issues one.</summary>
public readonly record struct SystemId
{
    static int next;

    internal SystemId(int value) => Value = value;

    /// <summary>The id's number; 0 for <c>default</c>, which names no system.</summary>
    public int Value { get; }

    /// <summary>A process-wide id no other caller of <see cref="Next"/> receives. Never <c>default</c>.</summary>
    public static SystemId Next() => new(Interlocked.Increment(ref next));
}
