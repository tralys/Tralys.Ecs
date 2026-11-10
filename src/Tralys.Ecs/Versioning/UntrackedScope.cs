namespace Tralys.Ecs;

/// <summary>An open <see cref="World.Untracked"/> scope; scopes nest, and stamping resumes once the outermost is disposed.</summary>
[MustDispose("writes stay unstamped for the rest of the world's life")]
public ref struct UntrackedScope
{
    World? world;

    internal UntrackedScope(World world) => this.world = world;

    /// <summary>Closes the scope; a second dispose does nothing.</summary>
    public void Dispose()
    {
        if (world is null)
            return;
        world.untrackedDepth--;
        world = null;
    }
}
