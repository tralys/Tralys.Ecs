namespace Tralys.Ecs;

/// <summary>Declares a <see cref="ChangeWatch{TBits}"/>'s inputs: what to watch, which bits a change of it owes, and where they are
/// marked.</summary>
public sealed class WatchBuilder<TBits>
    where TBits : unmanaged, Enum
{
    readonly Registry registry;
    internal readonly List<WatchInput> Inputs = [];
    internal readonly List<(Func<World, Entity, bool> Holds, ulong Bits)> Owners = [];

    internal WatchBuilder(Registry registry) => this.registry = registry;

    /// <summary>Owes <paramref name="bits"/> where <typeparamref name="T"/> was written, added, removed, enabled or disabled. A write
    /// or add inside <see cref="World.Untracked"/> stamps nothing and is not reported, except a tag's add, which only the structural changes carry.</summary>
    public WatchBuilder<TBits> Component<T>(TBits bits, Hop hop = default)
        where T : struct, IComponent, ITracked =>
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
        where TRow : struct, IRow<TKey>, ITracked =>
        Add(
            registry.IndexOf<TRow>(),
            ChangedOwners<TKey, TRow>.Collect,
            LogKinds.Of(LogKind.RowRemoved),
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
            LogKinds.Of(LogKind.Related, LogKind.Unrelated, LogKind.RelationChanged),
            bits,
            hop
        );

    /// <summary>Owes <paramref name="bits"/> where <typeparamref name="T"/> was added, removed, enabled or disabled, from the structural
    /// changes alone, so <typeparamref name="T"/> needs no stamps; value writes are not seen.</summary>
    public WatchBuilder<TBits> Presence<T>(TBits bits, Hop hop = default)
        where T : struct, IComponent => Add(registry.IndexOf<T>(), null, TagEntries, bits, hop);

    /// <summary>Owes <paramref name="bits"/> on each entity holding <typeparamref name="TOwner"/> whose
    /// <see cref="ChangeTracking.DescendantVersionOf"/> rose since the last collect, a descendant leaving included. An owner pending
    /// destroy or detached when the watch collects is skipped, and one gaining <typeparamref name="TOwner"/> is not marked for it.</summary>
    public WatchBuilder<TBits> Descendants<TOwner>(TBits bits)
        where TOwner : struct, IComponent
    {
        Owners.Add((OwnerTest<TOwner>.Holds, ChangeSet<TBits>.Bits(bits)));
        return this;
    }

    // Stamps carry adds and writes and leave out a write inside Untracked(); the structural changes add only what no stamp can say.
    static readonly LogKinds ComponentEntries = LogKinds.Of(
        LogKind.Removed,
        LogKind.Enabled,
        LogKind.Disabled
    );

    static readonly LogKinds TagEntries = ComponentEntries | LogKinds.Of(LogKind.Added);

    WatchBuilder<TBits> Add(
        int type,
        WatchInput.Collector? changed,
        LogKinds entries,
        TBits bits,
        Hop hop
    )
    {
        Inputs.Add(new WatchInput(type, changed, entries, ChangeSet<TBits>.Bits(bits), hop.Route));
        return this;
    }

    static class OwnerTest<TOwner>
        where TOwner : struct, IComponent
    {
        public static readonly Func<World, Entity, bool> Holds = (world, entity) =>
            world.Has<TOwner>(entity);
    }

    static class ChangedHolders<T>
        where T : struct, IComponent, ITracked
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
        where TRow : struct, IRow<TKey>, ITracked
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
    LogKinds Entries,
    ulong Bits,
    Action<World, Entity, List<Entity>>? Route
)
{
    internal delegate void Collector(in ChangeSample sample, List<Entity> into);
}
