namespace Tralys.Ecs;

/// <summary>What an observer callback receives besides the event: the world and why the change happened.</summary>
public readonly ref struct ObserverContext
{
    internal ObserverContext(World world, LifecycleReason reason)
    {
        World = world;
        Reason = reason;
    }

    /// <summary>The world the event happened in; callbacks run inside an iteration, so structural changes made through it wait for playback.</summary>
    public World World { get; }

    /// <summary>Why the change happened: the reason the operation was given, such as the destroy that released an edge or a parent link; <see cref="LifecycleReason.None"/> for replace events.</summary>
    public LifecycleReason Reason { get; }
}
