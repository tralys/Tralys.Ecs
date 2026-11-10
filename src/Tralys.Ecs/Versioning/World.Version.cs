namespace Tralys.Ecs;

public sealed partial class World
{
    internal ulong version = 1;
    internal int untrackedDepth;
    internal int runSequence;
    internal SystemId currentSystem;

    /// <summary>The world's change version. It advances before every system run, every playback and every cursor read, so a write is never stamped with a version a consumer has already read.</summary>
    public ulong Version => version;

    internal bool Tracking => untrackedDepth == 0;

    /// <summary>Advances <see cref="Version"/> outside any run, so later writes stamp a version newer than one read from
    /// <see cref="Version"/> before the call.</summary>
    [Effect(Effects.Mutates)]
    public void AdvanceVersion()
    {
        CheckWritable();
        version++;
    }

    /// <summary>Writes inside the scope stamp nothing; indexes are still maintained.</summary>
    [Effect(Effects.Mutates)]
    public UntrackedScope Untracked()
    {
        CheckWritable();
        untrackedDepth++;
        return new UntrackedScope(this);
    }

    /// <summary>Opens a system run: advances the version and attributes work to <paramref name="system"/> until disposed.</summary>
    [Effect(Effects.Mutates)]
    public SystemRunScope BeginSystemRun(SystemId system)
    {
        CheckWritable();
        version++;
        runSequence++;
        var scope = new SystemRunScope(this, currentSystem);
        currentSystem = system;
        return scope;
    }

    internal SystemRunScope EnsureSystemRun(SystemId system) =>
        currentSystem == default ? BeginSystemRun(system) : default;

    /// <summary>The system the open run attributes work to; <c>default</c> outside any run.</summary>
    public SystemId CurrentSystem => currentSystem;
}
