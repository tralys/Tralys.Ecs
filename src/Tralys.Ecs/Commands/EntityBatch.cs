using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>Changes to one entity that land in a single archetype move on dispose, from <see cref="World.Batch"/>. Each call is checked
/// as it is made, requirements and singleton claims against the batch so far, and reads by handle see it at once. Adds and removes wait
/// for dispose, played back as one entity's deferred commands are, so an add and a later remove of one type cancel out; overwrites of
/// components the entity already has apply at once. World writes to the entity while the batch is open join it, a query walk run
/// meanwhile leaves its changes for the next playback, and <see cref="World.Playback"/> throws until it is disposed. Inside a query walk everything waits until the walk closes; inside an observer, or while
/// the entity already has changes waiting for playback, everything waits for the next playback instead; so does the batch when it holds
/// a reparent, an edge or a cancelled destroy, or a destroy while another entity's reparent or edge waits, which the playback applies in
/// order with the rest.</summary>
[MustDispose("its adds and removes wait for the next playback")]
public ref struct EntityBatch
{
    World? world;
    readonly Entity entity;

    // Negative when the batch's changes wait for playback rather than its dispose.
    readonly int epoch;

    internal EntityBatch(World world, Entity entity, int epoch)
    {
        this.world = world;
        this.entity = entity;
        this.epoch = epoch;
    }

    readonly World Bound => world ?? throw new ObjectDisposedException(nameof(EntityBatch));

    /// <summary>Adds or overwrites <typeparamref name="T"/> as <see cref="World.Set{T}"/> does, an add waiting for dispose.</summary>
    /// <exception cref="InvalidOperationException">A requirement or singleton claim fails, or the batch is disposed.</exception>
    [Effect(Effects.Structural | Effects.Mutates)]
    public readonly void Set<T>(
        in T value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent => Bound.WriteBatched(entity, value, file, line);

    /// <summary>Adds the tag <typeparamref name="T"/>, waiting for dispose.</summary>
    /// <exception cref="InvalidOperationException">A requirement or singleton claim fails, or the batch is disposed.</exception>
    [Effect(Effects.Structural | Effects.Mutates)]
    public readonly void Add<T>([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        where T : struct, ITag => Bound.WriteBatched(entity, default(T), file, line);

    /// <summary>Removes <typeparamref name="T"/>, waiting for dispose, and returns false when the entity stores none, enabled or
    /// disabled, counting the batch so far.</summary>
    /// <exception cref="ObjectDisposedException">The batch is disposed.</exception>
    [Effect(Effects.Structural | Effects.Mutates)]
    public readonly bool Remove<T>(
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent => Bound.RemoveBatched<T>(entity, file, line);

    /// <summary>Applies the waiting adds and removes in one move, unless they wait for playback. A <c>using</c> cannot tell it the body
    /// threw, so a failure here replaces the body's exception.</summary>
    /// <exception cref="IndexConflictException">A unique index declared with <see cref="ConflictPolicy.Throw"/> saw a
    /// conflict.</exception>
    /// <exception cref="TargetPolicyException">A <see cref="World.Destroy"/> made while the batch was open would break a
    /// <see cref="TargetPolicy.Throw"/> relation; the entity was spared and the batch's other changes landed.</exception>
    /// <exception cref="AggregateException">More than one of the failures above.</exception>
    [Effect(Effects.MovesEntities | Effects.Mutates | Effects.MovesRowBlocks)]
    public void Dispose()
    {
        if (world is null)
            return;
        var target = world;
        world = null;
        if (epoch >= 0)
            target.CloseBatch(entity, epoch);
    }
}
