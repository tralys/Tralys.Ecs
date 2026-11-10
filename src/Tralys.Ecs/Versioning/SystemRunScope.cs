namespace Tralys.Ecs;

/// <summary>An open system run from <see cref="World.BeginSystemRun(SystemId)"/>; disposing it closes the run.</summary>
[MustDispose("the run never closes, so later work is attributed to its system")]
public ref struct SystemRunScope
{
    World? world;
    readonly SystemId previous;

    internal SystemRunScope(World world, SystemId previous)
    {
        this.world = world;
        this.previous = previous;
    }

    /// <summary>Closes the run, handing attribution back to the run it was opened inside; a second dispose does nothing.</summary>
    public void Dispose()
    {
        if (world is null)
            return;
        world.currentSystem = previous;
        world = null;
    }
}
