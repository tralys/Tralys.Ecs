namespace Tralys.Ecs;

/// <summary>A query body run once per matched chunk. Kernels run by <see cref="CachedQuery.RunParallel{TKernel}"/> share one value across
/// workers, so they keep no per-run state of their own.</summary>
public interface IChunkKernel
{
    /// <summary>Called once per matched chunk with a view of it.</summary>
    void Execute(ref ChunkView chunk);
}

/// <summary>A query body that folds rows into a per-chunk partial; partials are combined in chunk order, so the result does not depend on how
/// chunks were spread across workers.</summary>
public interface IReducingKernel<TAccumulator>
    where TAccumulator : struct, IReduction<TAccumulator>
{
    /// <summary>Called once per matched chunk; folds its visited rows into <paramref name="partial"/>, which starts as <see cref="IReduction{TSelf}.Identity"/>.</summary>
    void Execute(ref ChunkView chunk, ref TAccumulator partial);
}

/// <summary>An accumulator a query can reduce into.</summary>
public interface IReduction<TSelf>
    where TSelf : struct, IReduction<TSelf>
{
    /// <summary>The value that combining leaves unchanged; every partial starts from it.</summary>
    static abstract TSelf Identity { get; }

    /// <summary>Combines two partials, <paramref name="left"/> earlier in chunk order; it must be associative.</summary>
    static abstract TSelf Combine(in TSelf left, in TSelf right);
}

/// <summary>The act half of a collect-then-act run, called once per collected entity that is still eligible.</summary>
public interface IEntityAction
{
    /// <summary>Called for one collected entity that is still eligible, outside the iteration, so structural changes apply at once.</summary>
    void Act(Entity entity);
}
