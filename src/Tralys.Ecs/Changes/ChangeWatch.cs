namespace Tralys.Ecs;

/// <summary>A change cursor that turns its declared inputs into marks on the entities owing work: it samples, routes each change through
/// its <see cref="Hop"/>, marks the whole of an entity back from detached, pending destroy or a restore, marks everything when its log was
/// trimmed, forgets destroyed entities and commits, so its consumer never walks the log itself.</summary>
public sealed class ChangeWatch<TBits> : IDisposable
    where TBits : unmanaged, Enum
{
    readonly World world;
    readonly CursorId cursor;
    readonly WatchInput[] inputs;
    readonly WatchInput[]?[] byType;
    readonly (QueryDesc Owners, ulong Bits)[] descendants;
    readonly Dictionary<Entity, ulong>[] seen;
    readonly Action<World, ChangeSet<TBits>> everything;
    readonly Func<World, Entity, TBits> whole;
    readonly List<Entity> holders = [];
    readonly List<Entity> routed = [];
    Dictionary<Entity, ulong> fresh = [];

    internal ChangeWatch(
        World world,
        string name,
        WatchBuilder<TBits> builder,
        Action<World, ChangeSet<TBits>> everything,
        Func<World, Entity, TBits> whole
    )
    {
        this.world = world;
        this.everything = everything;
        this.whole = whole;
        inputs = [.. builder.Inputs];
        descendants = [.. builder.Owners];
        seen = new Dictionary<Entity, ulong>[descendants.Length];
        for (var i = 0; i < seen.Length; i++)
            seen[i] = [];

        var types = inputs.Select(input => input.Type).Distinct().ToArray();
        byType = new WatchInput[]?[types.Length == 0 ? 0 : types.Max() + 1];
        foreach (var type in types)
            byType[type] = [.. inputs.Where(input => input.Type == type)];
        cursor = world.Changes.Register(name, new CursorOptions(CategoryMask.All, types));
    }

    /// <summary>Marks into <paramref name="into"/> what changed since the last collect, and forgets each entity destroyed since.</summary>
    public void Collect(ChangeSet<TBits> into)
    {
        var changes = world.Changes;
        var sample = changes.Sample(cursor);
        if (sample.Status == CursorStatus.Expired)
            everything(world, into);
        else
        {
            foreach (var input in inputs)
            {
                if (input.Changed is not { } changed)
                    continue;
                changed(in sample, holders);
                foreach (var holder in holders)
                    Route(in input, holder, into);
                holders.Clear();
            }

            foreach (ref readonly var entry in sample.Log)
            {
                if (entry.TypeIndex >= 0)
                {
                    if (entry.TypeIndex < byType.Length && byType[entry.TypeIndex] is { } typed)
                        foreach (var input in typed)
                            if ((input.Entries & (1u << (int)entry.Kind)) != 0)
                                Route(in input, entry.Entity, into);
                }
                else if (
                    entry.Kind is LogKind.Reattached or LogKind.DestroyCancelled or LogKind.Restored
                )
                    Mark(into, entry.Entity, ChangeSet<TBits>.Bits(whole(world, entry.Entity)));
                else if (entry.Kind is LogKind.Destroyed or LogKind.Extracted)
                    into.Forget(entry.Entity);
            }
        }

        for (var i = 0; i < descendants.Length; i++)
            ScanDescendants(i, into);
        changes.Commit(cursor, sample.Until);
    }

    /// <summary>Unregisters the watch's cursor, so it no longer holds the log back; the watch is dead from then on.</summary>
    public void Dispose() => world.Changes.Unregister(cursor);

    void Route(in WatchInput input, Entity holder, ChangeSet<TBits> into)
    {
        if (input.Route is not { } route)
        {
            Mark(into, holder, input.Bits);
            return;
        }
        if (!world.Exists(holder))
            return;
        route(world, holder, routed);
        foreach (var entity in routed)
            Mark(into, entity, input.Bits);
        routed.Clear();
    }

    void ScanDescendants(int index, ChangeSet<TBits> into)
    {
        var (owners, bits) = descendants[index];
        var last = seen[index];
        foreach (var chunk in world.Query(owners))
        foreach (var row in chunk.Rows)
        {
            var owner = chunk.Entities[row];
            var version = world.Changes.DescendantVersionOf(owner);
            if (!last.TryGetValue(owner, out var was) || was != version)
                Mark(into, owner, bits);
            fresh[owner] = version;
        }
        last.Clear();
        seen[index] = fresh;
        fresh = last;
    }

    void Mark(ChangeSet<TBits> into, Entity entity, ulong bits)
    {
        if (bits != 0 && !entity.IsDefault && world.Exists(entity))
            into.Mark(entity, ChangeSet<TBits>.From(bits));
    }
}
