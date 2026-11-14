namespace Tralys.Ecs;

/// <summary>Declares a schedule: its <see cref="SetAttribute{TSet}"/> sets in run order and its named playback points. Declare it
/// <c>partial</c>: the generator checks its points and adds a static <c>Declaration</c> listing the systems in its sets.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ScheduleAttribute : Attribute;

/// <summary>A set of the schedule; sets run in the order these attributes are written.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class SetAttribute<TSet> : Attribute
    where TSet : class;

/// <summary>A named playback point of the schedule, placed right after <see cref="After"/>, or right before <see cref="Before"/> when no
/// <see cref="After"/> is given; with neither, it opens the tick.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PlaybackPointAttribute(string name) : Attribute
{
    /// <summary>The point's name, unique within the schedule; a failed playback there reports it to
    /// <see cref="IFaultSink.OnPlaybackFault"/>.</summary>
    public string Name { get; } = name;

    /// <summary>A system or set the point follows: after the system, or after the set's last system.</summary>
    public Type? After { get; set; }

    /// <summary>A system or set the point precedes: before the system, or before the set's first system; the build fails if
    /// <see cref="After"/> runs later.</summary>
    public Type? Before { get; set; }

    /// <summary>The point finalises pending destroys, and the end of the tick no longer does.</summary>
    public bool FinalizesDestroys { get; set; }
}

/// <summary>A schedule's sets, named playback points and the systems in its sets, as <see cref="ScheduleAttribute"/> generates it.</summary>
public sealed class ScheduleDeclaration(
    string name,
    Type[] sets,
    PlaybackPointDeclaration[] points,
    Type[]? declaredOrder = null
)
{
    /// <summary>The schedule's name, used in build and run errors.</summary>
    public string Name { get; } = name;

    /// <summary>The sets in run order.</summary>
    public IReadOnlyList<Type> Sets { get; } = sets;

    /// <summary>The named playback points; the build rejects a repeated name or a second point that finalises destroys.</summary>
    public IReadOnlyList<PlaybackPointDeclaration> Points { get; } = points;

    /// <summary>The systems the generator saw in the schedule's sets; a builder fails when one of them was not added.</summary>
    public IReadOnlyList<Type> DeclaredOrder { get; } = declaredOrder ?? [];

    internal int IndexOf(Type set)
    {
        for (var i = 0; i < Sets.Count; i++)
        {
            if (Sets[i] == set)
                return i;
        }
        return -1;
    }
}

/// <summary>A named playback point, as <see cref="PlaybackPointAttribute"/> generates it.</summary>
public sealed class PlaybackPointDeclaration(
    string name,
    Type[] after,
    Type[] before,
    bool finalizesDestroys = false
)
{
    /// <inheritdoc cref="PlaybackPointAttribute.Name"/>
    public string Name { get; } = name;

    /// <summary>The systems and sets the point follows; it runs right after the latest of them.</summary>
    public IReadOnlyList<Type> After { get; } = after;

    /// <summary>The systems and sets the point precedes; without <see cref="After"/>, it runs right before the earliest of them.</summary>
    public IReadOnlyList<Type> Before { get; } = before;

    /// <inheritdoc cref="PlaybackPointAttribute.FinalizesDestroys"/>
    public bool FinalizesDestroys { get; } = finalizesDestroys;
}
