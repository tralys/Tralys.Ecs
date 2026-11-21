using System.Runtime.InteropServices;

namespace Tralys.Ecs;

/// <summary>A change cursor that turns its declared inputs into marks on the entities owing work: it samples, routes each change through
/// its <see cref="Hop"/>, marks the whole of an entity back from detached, pending destroy or a restore, marks everything when its queue
/// overflowed or the world was reset to a snapshot, forgets destroyed entities and commits, so its consumer never walks the changes
/// itself.</summary>
public sealed class ChangeWatch<TBits> : IDisposable
    where TBits : unmanaged, Enum
{
    readonly World world;
    readonly CursorId cursor;
    internal readonly WatchQueue queue;
    readonly WatchInput[] inputs;
    readonly WatchInput[]?[] byType;
    readonly (Func<World, Entity, bool> Holds, ulong Bits)[] owners;
    readonly Action<World, ChangeSet<TBits>> everything;
    readonly Func<World, Entity, TBits> whole;
    readonly List<Entity> holders = [];
    readonly List<Entity> routed = [];

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
        owners = [.. builder.Owners];

        var types = inputs.Select(input => input.Type).Distinct().ToArray();
        byType = new WatchInput[]?[types.Length == 0 ? 0 : types.Max() + 1];
        var kinds = new LogKinds[byType.Length];
        foreach (var type in types)
        {
            WatchInput[] ofType = [.. inputs.Where(input => input.Type == type)];
            byType[type] = ofType;
            foreach (var input in ofType)
                kinds[type] |= input.Entries;
        }
        cursor = world.Changes.Register(name);
        queue = world.changes.Subscribe(cursor, kinds, owners.Length > 0);
    }

    /// <summary>Marks into <paramref name="into"/> what changed since the last collect, and forgets each entity destroyed since.</summary>
    public void Collect(ChangeSet<TBits> into)
    {
        var changes = world.Changes;
        var sample = changes.Sample(cursor);
        if (queue.Overflowed)
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

            foreach (ref readonly var change in CollectionsMarshal.AsSpan(queue.Events))
            {
                if (change.Type >= 0)
                {
                    if (byType[change.Type] is { } typed && world.Admits(change.Entity, default))
                        foreach (var input in typed)
                            if (input.Entries.Has(change.Kind))
                                Route(in input, change.Entity, into);
                }
                else if (WatchQueue.Resyncs.Has(change.Kind))
                    Mark(into, change.Entity, ChangeSet<TBits>.Bits(whole(world, change.Entity)));
                else
                    into.Forget(change.Entity);
            }

            foreach (var owner in queue.Raised)
            {
                if (!world.Exists(owner) || !world.Admits(owner, default))
                    continue;
                foreach (var (holds, bits) in owners)
                    if (holds(world, owner))
                        Mark(into, owner, bits);
            }
        }
        queue.Clear();
        changes.Commit(cursor, sample.Until);
    }

    /// <summary>Unregisters the watch's cursor and stops its queue; the watch is dead from then on.</summary>
    public void Dispose()
    {
        world.changes.Unsubscribe(queue);
        world.Changes.Unregister(cursor);
    }

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

    void Mark(ChangeSet<TBits> into, Entity entity, ulong bits)
    {
        if (bits != 0 && !entity.IsDefault && world.Exists(entity))
            into.Mark(entity, ChangeSet<TBits>.From(bits));
    }
}
