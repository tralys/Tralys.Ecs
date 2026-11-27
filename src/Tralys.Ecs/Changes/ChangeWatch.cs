using System.Runtime.InteropServices;

namespace Tralys.Ecs;

/// <summary>A change cursor that turns its declared inputs into marks on the entities owing work: it samples, routes each change through
/// its <see cref="Hop"/>, marks the whole of an entity back from detached, pending destroy or a restore and routes each input's hop from
/// it, marks everything when its queue
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
    bool disposed;

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

    /// <summary>Marks into <paramref name="into"/> what changed since the last collect, and forgets each entity destroyed since. An entity
    /// gone, pending destroy or detached as the watch collects is marked for nothing, and the scope is never asked about one. What the
    /// scope or a route changes during the collect is marked at the next; if one throws, the next collect marks everything.</summary>
    [Effect(Effects.Mutates)]
    public void Collect(ChangeSet<TBits> into)
    {
        var changes = world.Changes;
        var sample = changes.Sample(cursor);
        into.Join(world.changes);
        var overflowed = queue.Take(out var events, out var raised);
        try
        {
            if (overflowed)
            {
                everything(world, into);
                into.ForgetGone(world);
            }
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

                foreach (ref readonly var change in CollectionsMarshal.AsSpan(events))
                {
                    if (WatchQueue.Forgets.Has(change.Kind))
                        into.Forget(change.Entity);
                    else if (!Admitted(change.Entity))
                        continue;
                    else if (change.Type < 0)
                        Resync(change.Entity, into);
                    else if (byType[change.Type] is { } typed)
                    {
                        foreach (var input in typed)
                            if (input.Entries.Has(change.Kind))
                                Route(in input, change.Entity, into);
                    }
                }

                foreach (var owner in raised)
                {
                    if (!Admitted(owner))
                        continue;
                    foreach (var (holds, bits) in owners)
                        if (holds(world, owner))
                            Mark(into, owner, bits);
                }
            }
        }
        catch
        {
            holders.Clear();
            routed.Clear();
            queue.Overflow();
            throw;
        }
        changes.Commit(cursor, sample.Window);
    }

    /// <summary>Unregisters the watch's cursor and stops its queue; the watch is dead from then on, and a second dispose does
    /// nothing.</summary>
    [Effect(Effects.Mutates)]
    public void Dispose()
    {
        if (disposed)
            return;
        world.changes.Unsubscribe(queue);
        world.Changes.Unregister(cursor);
        disposed = true;
    }

    bool Admitted(Entity entity) => world.Exists(entity) && world.Admits(entity, default);

    // Changes made while the entity was away reached no route, so each input owes its hop's entities as if it had changed.
    void Resync(Entity entity, ChangeSet<TBits> into)
    {
        Mark(into, entity, ChangeSet<TBits>.Bits(whole(world, entity)));
        foreach (var input in inputs)
        {
            if (input.Route is not null)
                Route(in input, entity, into);
        }
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
        if (bits != 0 && !entity.IsDefault && Admitted(entity))
            into.Mark(entity, ChangeSet<TBits>.From(bits));
    }
}
