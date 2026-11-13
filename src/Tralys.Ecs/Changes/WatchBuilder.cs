namespace Tralys.Ecs;

/// <summary>Declares a <see cref="ChangeWatch{TBits}"/>'s inputs: what to watch, which bits a change of it owes, and where they are
/// marked.</summary>
public sealed class WatchBuilder<TBits>
    where TBits : unmanaged, Enum
{
    readonly Registry registry;
    internal readonly List<WatchInput> Inputs = [];
    internal readonly List<(QueryDesc Owners, ulong Bits)> Owners = [];

    internal WatchBuilder(Registry registry) => this.registry = registry;

    /// <summary>Owes <paramref name="bits"/> where <typeparamref name="T"/> was written, added, removed, enabled or disabled. A write
    /// or add inside <see cref="World.Untracked"/> stamps nothing and is not reported, except a tag's add, which only the log carries.</summary>
    public WatchBuilder<TBits> Component<T>(TBits bits, Hop hop = default)
        where T : struct, IComponent =>
        Add(
            registry.IndexOf<T>(),
            ChangedHolders<T>.Collect,
            registry.Get<T>().IsTag ? TagEntries : ComponentEntries,
            bits,
            hop
        );

    /// <summary>Owes <paramref name="bits"/> where a <typeparamref name="TRow"/> row was written, added or removed; marked on its owner.
    /// A write or add inside <see cref="World.Untracked"/> stamps nothing and is not reported.</summary>
    public WatchBuilder<TBits> Rows<TKey, TRow>(TBits bits, Hop hop = default)
        where TKey : notnull
        where TRow : struct, IRow<TKey> =>
        Add(
            registry.IndexOf<TRow>(),
            ChangedOwners<TKey, TRow>.Collect,
            Entries(LogKind.RowRemoved),
            bits,
            hop
        );

    /// <summary>Owes <paramref name="bits"/> where an edge of <typeparamref name="R"/> was made, broken or given another payload; marked
    /// on its source.</summary>
    public WatchBuilder<TBits> Relation<R>(TBits bits, Hop hop = default)
        where R : struct, IRelation =>
        Add(
            registry.IndexOf<R>(),
            null,
            Entries(LogKind.Related, LogKind.Unrelated, LogKind.RelationChanged),
            bits,
            hop
        );

    /// <summary>Owes <paramref name="bits"/> on each entity <paramref name="owners"/> matches whose descendants changed as
    /// <see cref="ChangeTracking.DescendantVersionOf"/> counts it, a descendant leaving included; the first collect marks every one.</summary>
    public WatchBuilder<TBits> Descendants(TBits bits, QueryDesc owners)
    {
        Owners.Add((owners, ChangeSet<TBits>.Bits(bits)));
        return this;
    }

    // Stamps carry adds and writes and leave out a write inside Untracked(); the log adds only what no stamp can say.
    static readonly uint ComponentEntries = Entries(
        LogKind.Removed,
        LogKind.Enabled,
        LogKind.Disabled
    );

    static readonly uint TagEntries = ComponentEntries | Entries(LogKind.Added);

    static uint Entries(params LogKind[] kinds)
    {
        var mask = 0u;
        foreach (var kind in kinds)
            mask |= 1u << (int)kind;
        return mask;
    }

    WatchBuilder<TBits> Add(
        int type,
        WatchInput.Collector? changed,
        uint entries,
        TBits bits,
        Hop hop
    )
    {
        Inputs.Add(new WatchInput(type, changed, entries, ChangeSet<TBits>.Bits(bits), hop.Route));
        return this;
    }

    static class ChangedHolders<T>
        where T : struct, IComponent
    {
        public static readonly WatchInput.Collector Collect = Run;

        static void Run(in ChangeSample sample, List<Entity> into)
        {
            foreach (var changed in sample.Changed<T>())
                into.Add(changed.Entity);
        }
    }

    static class ChangedOwners<TKey, TRow>
        where TKey : notnull
        where TRow : struct, IRow<TKey>
    {
        public static readonly WatchInput.Collector Collect = Run;

        static void Run(in ChangeSample sample, List<Entity> into)
        {
            foreach (var row in sample.ChangedRows<TKey, TRow>())
                into.Add(row.Entity);
        }
    }
}

readonly record struct WatchInput(
    int Type,
    WatchInput.Collector? Changed,
    uint Entries,
    ulong Bits,
    Action<World, Entity, List<Entity>>? Route
)
{
    internal delegate void Collector(in ChangeSample sample, List<Entity> into);
}
