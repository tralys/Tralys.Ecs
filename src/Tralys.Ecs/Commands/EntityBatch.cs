using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>Changes to one entity that land in a single archetype move on dispose, from <see cref="World.Batch"/>. Each call is checked
/// as it is made, requirements and singleton claims against the batch so far, and reads by handle see it at once. Adds and removes wait
/// for dispose, played back as one entity's deferred commands are, so an add and a later remove of one type cancel out; overwrites of
/// components the entity already has apply at once. World writes to the entity while the batch is open join it.
/// Inside an observer, or while the entity already has changes waiting for playback, everything waits for the next playback
/// instead.</summary>
[MustDispose("its adds and removes wait for the next playback")]
public ref struct EntityBatch
{
    World? world;
    readonly Entity entity;
    readonly bool applies;

    internal EntityBatch(World world, Entity entity, bool applies)
    {
        this.world = world;
        this.entity = entity;
        this.applies = applies;
    }

    readonly World Bound => world ?? throw new ObjectDisposedException(nameof(EntityBatch));

    /// <summary>Adds or overwrites <typeparamref name="T"/> as <see cref="World.Set{T}"/> does, an add waiting for dispose.</summary>
    /// <exception cref="InvalidOperationException">A requirement or singleton claim fails, or the batch is disposed.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public readonly void Set<T>(
        in T value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent => Bound.Set(entity, value, file: file, line: line);

    /// <summary>Adds the tag <typeparamref name="T"/>, waiting for dispose.</summary>
    /// <exception cref="InvalidOperationException">A requirement or singleton claim fails, or the batch is disposed.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public readonly void Add<T>([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        where T : struct, ITag => Bound.Add<T>(entity, file: file, line: line);

    /// <summary>Removes <typeparamref name="T"/>, waiting for dispose, and returns false when the entity stores none, enabled or
    /// disabled, counting the batch so far.</summary>
    /// <exception cref="ObjectDisposedException">The batch is disposed.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public readonly bool Remove<T>(
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent => Bound.Remove<T>(entity, file: file, line: line);

    /// <summary>Applies the waiting adds and removes in one move, unless they wait for playback.</summary>
    /// <exception cref="IndexConflictException">A unique index declared with <see cref="ConflictPolicy.Throw"/> saw a
    /// conflict.</exception>
    /// <exception cref="TargetPolicyException">A <see cref="World.Destroy"/> made while the batch was open would break a
    /// <see cref="TargetPolicy.Throw"/> relation; the entity was spared and the batch's other changes landed.</exception>
    /// <exception cref="AggregateException">Both of the failures above.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public void Dispose()
    {
        if (world is null)
            return;
        var target = world;
        world = null;
        if (applies)
            target.CloseBatch(entity);
    }
}
