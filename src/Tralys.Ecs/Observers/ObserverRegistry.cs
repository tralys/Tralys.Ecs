using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Tralys.Ecs;

/// <summary>Names a registered observer for <see cref="ObserverRegistry.Remove"/>; a registry never reuses one.</summary>
/// <param name="Value">The registry's number for the observer.</param>
public readonly record struct ObserverId(int Value);

internal enum EventKind : byte
{
    Add,
    Replay,
    Remove,
    Replace,
    Lifecycle,
    Related,
    Unrelated,
    RelationChanged,
    Hierarchy,
}

internal struct ObserverEvent
{
    public EventKind Kind;
    public Entity Entity;
    public Entity Other;
    public HierarchyEvent Hierarchy;
    public LogKind Lifecycle;
    public int Type;
    public int Old;
    public int New;
    public int Recipient;
    public LifecycleReason Reason;
}

internal abstract class TypeObservers
{
    public abstract bool WantsAdd { get; }
    public abstract bool WantsRemove { get; }
    public abstract bool WantsReplace { get; }
    public abstract bool WantsRelation { get; }
    public abstract PayloadStore Values { get; }

    public abstract void Deliver(World world, in ObserverEvent e, ref List<Exception>? errors);

    public abstract bool Remove(int id);
}

internal interface IReplaceSink<T>
    where T : struct
{
    void OnReplace(in ObserverContext context, Entity entity, in T oldValue, in T newValue);
}

internal sealed class ReplaceSink<T>(IReplaceObserver<T> observer) : IReplaceSink<T>
    where T : struct, IReplaceOnly
{
    public void OnReplace(
        in ObserverContext context,
        Entity entity,
        in T oldValue,
        in T newValue
    ) => observer.OnReplace(context, entity, oldValue, newValue);
}

internal sealed class TypeObservers<T> : TypeObservers
    where T : struct
{
    public (int Id, IAddObserver<T> Observer)[] Adds = [];
    public (int Id, IRemoveObserver<T> Observer)[] Removes = [];
    public (int Id, IReplaceSink<T> Observer)[] Replaces = [];
    public (int Id, IRelationObserver<T> Observer)[] Relations = [];
    public readonly PayloadStore<T> Store = new();

    public override bool WantsAdd => Adds.Length > 0;
    public override bool WantsRemove => Removes.Length > 0;
    public override bool WantsReplace => Replaces.Length > 0;
    public override bool WantsRelation => Relations.Length > 0;
    public override PayloadStore Values => Store;

    public override void Deliver(World world, in ObserverEvent e, ref List<Exception>? errors)
    {
        var context = new ObserverContext(world, e.Reason);
        switch (e.Kind)
        {
            case EventKind.Add:
                foreach (var (_, observer) in Adds)
                {
                    try
                    {
                        observer.OnAdd(context, e.Entity, Store[e.New]);
                    }
                    catch (Exception error)
                    {
                        (errors ??= []).Add(error);
                    }
                }
                break;
            case EventKind.Replay:
                foreach (var (id, observer) in Adds)
                {
                    if (id != e.Recipient)
                        continue;
                    try
                    {
                        observer.OnAdd(context, e.Entity, Store[e.New]);
                    }
                    catch (Exception error)
                    {
                        (errors ??= []).Add(error);
                    }
                }
                break;
            case EventKind.Remove:
                foreach (var (_, observer) in Removes)
                {
                    try
                    {
                        observer.OnRemove(context, e.Entity, Store[e.Old]);
                    }
                    catch (Exception error)
                    {
                        (errors ??= []).Add(error);
                    }
                }
                break;
            case EventKind.Replace:
                foreach (var (_, observer) in Replaces)
                {
                    try
                    {
                        observer.OnReplace(context, e.Entity, Store[e.Old], Store[e.New]);
                    }
                    catch (Exception error)
                    {
                        (errors ??= []).Add(error);
                    }
                }
                break;
            case EventKind.Related:
                foreach (var (_, observer) in Relations)
                {
                    try
                    {
                        observer.OnRelated(context, e.Entity, e.Other, Store[e.New]);
                    }
                    catch (Exception error)
                    {
                        (errors ??= []).Add(error);
                    }
                }
                break;
            case EventKind.Unrelated:
                foreach (var (_, observer) in Relations)
                {
                    try
                    {
                        observer.OnUnrelated(context, e.Entity, e.Other, Store[e.New]);
                    }
                    catch (Exception error)
                    {
                        (errors ??= []).Add(error);
                    }
                }
                break;
            case EventKind.RelationChanged:
                foreach (var (_, observer) in Relations)
                {
                    try
                    {
                        observer.OnRelationChanged(
                            context,
                            e.Entity,
                            e.Other,
                            Store[e.Old],
                            Store[e.New]
                        );
                    }
                    catch (Exception error)
                    {
                        (errors ??= []).Add(error);
                    }
                }
                break;
        }
    }

    public override bool Remove(int id)
    {
        var before = Adds.Length + Removes.Length + Replaces.Length + Relations.Length;
        Adds = Array.FindAll(Adds, o => o.Id != id);
        Removes = Array.FindAll(Removes, o => o.Id != id);
        Replaces = Array.FindAll(Replaces, o => o.Id != id);
        Relations = Array.FindAll(Relations, o => o.Id != id);
        return Adds.Length + Removes.Length + Replaces.Length + Relations.Length < before;
    }
}

/// <summary>Typed lifecycle observers. Every event, including those from immediate operations, is delivered at the next playback point, in operation order.</summary>
public sealed class ObserverRegistry
{
    readonly World world;
    readonly TypeObservers?[] byType;
    readonly List<TypeObservers> live = [];
    internal (int Id, IDestroyObserver Observer)[] destroys = [];
    internal (int Id, ILifecycleObserver Observer)[] lifecycles = [];
    internal (int Id, IHierarchyObserver Observer)[] hierarchies = [];
    internal bool AnyAdd;
    internal bool AnyRemove;
    ObserverEvent[] queue = new ObserverEvent[64];
    ObserverEvent[] delivering = new ObserverEvent[64];
    int queued;
    int nextId;

    internal ObserverRegistry(World world)
    {
        this.world = world;
        byType = new TypeObservers?[world.registry.IndexBound];
    }

    /// <summary>Components of <typeparamref name="T"/> added to or enabled on an entity, with the value. With <paramref name="replayExisting"/>,
    /// this observer alone also sees an add for every entity already holding an enabled one, at the next playback.</summary>
    public ObserverId OnAdd<T>(IAddObserver<T> observer, bool replayExisting = false)
        where T : struct, IComponent
    {
        var id = nextId++;
        var type = world.registry.IndexOf<T>();
        var observers = Of<T>(type);
        observers.Adds = [.. observers.Adds, (id, observer)];
        AnyAdd = true;
        if (replayExisting)
            world.ReplayAdds(type, id);
        return new ObserverId(id);
    }

    /// <summary>Components of <typeparamref name="T"/> removed from or disabled on an entity, including by its destroy, with the value; a
    /// disabled one leaves without another.</summary>
    public ObserverId OnRemove<T>(IRemoveObserver<T> observer)
        where T : struct, IComponent
    {
        var id = nextId++;
        var observers = Of<T>(world.registry.IndexOf<T>());
        observers.Removes = [.. observers.Removes, (id, observer)];
        AnyRemove = true;
        return new ObserverId(id);
    }

    /// <summary>Overwrites of the <see cref="ReplaceOnlyAttribute"/> component <typeparamref name="T"/> whose bytes differ, with both values.</summary>
    public ObserverId OnReplace<T>(IReplaceObserver<T> observer)
        where T : struct, IReplaceOnly
    {
        var id = nextId++;
        var observers = Of<T>(world.registry.IndexOf<T>());
        observers.Replaces = [.. observers.Replaces, (id, new ReplaceSink<T>(observer))];
        return new ObserverId(id);
    }

    /// <summary>Rows of <typeparamref name="T"/> added to an entity, with the stored row.</summary>
    public ObserverId OnRowAdd<T>(IAddObserver<T> observer)
        where T : struct, IRow
    {
        var id = nextId++;
        var observers = Of<T>(world.registry.IndexOf<T>());
        observers.Adds = [.. observers.Adds, (id, observer)];
        AnyAdd = true;
        return new ObserverId(id);
    }

    /// <summary>Rows of <typeparamref name="T"/> removed from an entity, with the removed row.</summary>
    public ObserverId OnRowRemove<T>(IRemoveObserver<T> observer)
        where T : struct, IRow
    {
        var id = nextId++;
        var observers = Of<T>(world.registry.IndexOf<T>());
        observers.Removes = [.. observers.Removes, (id, observer)];
        AnyRemove = true;
        return new ObserverId(id);
    }

    /// <summary>Entities about to be released by finalisation or by <see cref="World.Extract"/>, called while every component is still readable.</summary>
    public ObserverId OnDestroy(IDestroyObserver observer)
    {
        var id = nextId++;
        destroys = [.. destroys, (id, observer)];
        return new ObserverId(id);
    }

    /// <summary>Edges of relation <typeparamref name="R"/> made, broken or given a different payload.</summary>
    public ObserverId OnRelation<R>(IRelationObserver<R> observer)
        where R : struct, IRelation
    {
        var id = nextId++;
        var observers = Of<R>(world.registry.IndexOf<R>());
        observers.Relations = [.. observers.Relations, (id, observer)];
        return new ObserverId(id);
    }

    /// <summary>Children gaining, leaving or moving under a parent.</summary>
    public ObserverId OnHierarchy(IHierarchyObserver observer)
    {
        var id = nextId++;
        hierarchies = [.. hierarchies, (id, observer)];
        return new ObserverId(id);
    }

    /// <summary>Entities detached, reattached or restored.</summary>
    public ObserverId OnLifecycle(ILifecycleObserver observer)
    {
        var id = nextId++;
        lifecycles = [.. lifecycles, (id, observer)];
        return new ObserverId(id);
    }

    /// <summary>Unregisters the observer registered under <paramref name="id"/>.</summary>
    /// <exception cref="ArgumentException">No observer is registered under <paramref name="id"/>.</exception>
    public void Remove(ObserverId id)
    {
        var found = false;
        AnyAdd = AnyRemove = false;
        foreach (var observers in live)
        {
            found |= observers.Remove(id.Value);
            AnyAdd |= observers.WantsAdd;
            AnyRemove |= observers.WantsRemove;
        }
        found |= Drop(ref destroys, id.Value);
        found |= Drop(ref lifecycles, id.Value);
        found |= Drop(ref hierarchies, id.Value);
        if (!found)
            throw new ArgumentException($"Observer {id.Value} is not registered.", nameof(id));
    }

    static bool Drop<TObserver>(ref (int Id, TObserver Observer)[] observers, int id)
    {
        var kept = Array.FindAll(observers, o => o.Id != id);
        var dropped = kept.Length < observers.Length;
        observers = kept;
        return dropped;
    }

    TypeObservers<T> Of<T>(int type)
        where T : struct
    {
        if (byType[type] is { } existing)
            return Unsafe.As<TypeObservers<T>>(existing);
        var created = new TypeObservers<T>();
        byType[type] = created;
        live.Add(created);
        return created;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TypeObservers? For(int type) => byType[type];

    internal bool HasPending => queued > 0;

    internal void QueueAdd(Entity entity, int type, ref byte value, LifecycleReason reason)
    {
        if (byType[type] is { WantsAdd: true } observers)
            Enqueue(
                new ObserverEvent
                {
                    Kind = EventKind.Add,
                    Entity = entity,
                    Type = type,
                    New = observers.Values.Capture(ref value),
                    Reason = reason,
                }
            );
    }

    internal void QueueReplay(Entity entity, int type, ref byte value, int recipient) =>
        Enqueue(
            new ObserverEvent
            {
                Kind = EventKind.Replay,
                Entity = entity,
                Type = type,
                New = byType[type]!.Values.Capture(ref value),
                Recipient = recipient,
            }
        );

    // Copies the departing value now, before its slot is reused.
    internal void QueueRemove(Entity entity, int type, ref byte value, LifecycleReason reason)
    {
        if (byType[type] is { WantsRemove: true } observers)
            Enqueue(
                new ObserverEvent
                {
                    Kind = EventKind.Remove,
                    Entity = entity,
                    Type = type,
                    Old = observers.Values.Capture(ref value),
                    Reason = reason,
                }
            );
    }

    internal int CaptureDeparting(int type, ref byte value) =>
        byType[type] is { WantsRemove: true } observers ? observers.Values.Capture(ref value) : -1;

    internal void QueueCapturedRemove(Entity entity, int type, int captured, LifecycleReason reason)
    {
        if (captured >= 0)
            Enqueue(
                new ObserverEvent
                {
                    Kind = EventKind.Remove,
                    Entity = entity,
                    Type = type,
                    Old = captured,
                    Reason = reason,
                }
            );
    }

    internal void QueueReplace(Entity entity, int type, ref byte oldValue, ref byte newValue)
    {
        if (byType[type] is { WantsReplace: true } observers)
            Enqueue(
                new ObserverEvent
                {
                    Kind = EventKind.Replace,
                    Entity = entity,
                    Type = type,
                    Old = observers.Values.Capture(ref oldValue),
                    New = observers.Values.Capture(ref newValue),
                }
            );
    }

    internal (int Old, int New) CaptureReplace(int type, ref byte oldValue, ref byte newValue) =>
        byType[type] is { WantsReplace: true } observers
            ? (observers.Values.Capture(ref oldValue), observers.Values.Capture(ref newValue))
            : (-1, -1);

    internal void QueueCapturedReplace(Entity entity, int type, int oldValue, int newValue)
    {
        if (oldValue >= 0)
            Enqueue(
                new ObserverEvent
                {
                    Kind = EventKind.Replace,
                    Entity = entity,
                    Type = type,
                    Old = oldValue,
                    New = newValue,
                }
            );
    }

    internal void QueueLifecycle(Entity entity, LogKind kind, LifecycleReason reason)
    {
        if (lifecycles.Length > 0)
            Enqueue(
                new ObserverEvent
                {
                    Kind = EventKind.Lifecycle,
                    Entity = entity,
                    Lifecycle = kind,
                    Type = -1,
                    Reason = reason,
                }
            );
    }

    internal void QueueRelation(
        Entity source,
        Entity target,
        int type,
        ref byte payload,
        bool related,
        LifecycleReason reason
    )
    {
        if (byType[type] is { WantsRelation: true } observers)
            Enqueue(
                new ObserverEvent
                {
                    Kind = related ? EventKind.Related : EventKind.Unrelated,
                    Entity = source,
                    Other = target,
                    Type = type,
                    New = observers.Values.Capture(ref payload),
                    Reason = reason,
                }
            );
    }

    internal void QueueRelationChanged(
        Entity source,
        Entity target,
        int type,
        ref byte oldPayload,
        ref byte newPayload,
        LifecycleReason reason
    )
    {
        if (byType[type] is { WantsRelation: true } observers)
            Enqueue(
                new ObserverEvent
                {
                    Kind = EventKind.RelationChanged,
                    Entity = source,
                    Other = target,
                    Type = type,
                    Old = observers.Values.Capture(ref oldPayload),
                    New = observers.Values.Capture(ref newPayload),
                    Reason = reason,
                }
            );
    }

    internal void QueueHierarchy(
        Entity child,
        Entity parent,
        HierarchyEvent kind,
        LifecycleReason reason
    )
    {
        if (hierarchies.Length > 0)
            Enqueue(
                new ObserverEvent
                {
                    Kind = EventKind.Hierarchy,
                    Entity = child,
                    Other = parent,
                    Hierarchy = kind,
                    Type = -1,
                    Reason = reason,
                }
            );
    }

    void Enqueue(in ObserverEvent e)
    {
        if (queued == queue.Length)
            Array.Resize(ref queue, queued * 2);
        queue[queued++] = e;
    }

    // Events queued by observers during delivery wait for the next round.
    internal int Deliver()
    {
        var count = queued;
        if (count == 0)
            return 0;
        (queue, delivering) = (delivering, queue);
        if (queue.Length < delivering.Length)
            queue = new ObserverEvent[delivering.Length];
        queued = 0;
        List<Exception>? errors = null;
        for (var i = 0; i < count; i++)
        {
            ref readonly var e = ref delivering[i];
            if (e.Type >= 0)
                byType[e.Type]!.Deliver(world, e, ref errors);
            else if (e.Kind == EventKind.Hierarchy)
            {
                var context = new ObserverContext(world, e.Reason);
                foreach (var (_, observer) in hierarchies)
                {
                    try
                    {
                        observer.OnHierarchy(context, e.Entity, e.Other, e.Hierarchy);
                    }
                    catch (Exception error)
                    {
                        (errors ??= []).Add(error);
                    }
                }
            }
            else
            {
                var context = new ObserverContext(world, e.Reason);
                foreach (var (_, observer) in lifecycles)
                {
                    try
                    {
                        observer.OnLifecycle(context, e.Entity, e.Lifecycle);
                    }
                    catch (Exception error)
                    {
                        (errors ??= []).Add(error);
                    }
                }
            }
        }
        Rethrow(errors);
        return count;
    }

    internal static void Rethrow(List<Exception>? errors)
    {
        if (errors is { Count: 1 })
            ExceptionDispatchInfo.Throw(errors[0]);
        if (errors is not null)
            throw new AggregateException("More than one observer threw.", errors);
    }

    internal void Discard()
    {
        queued = 0;
        ResetStores();
    }

    internal void ResetStores()
    {
        foreach (var observers in live)
            observers.Values.Reset();
    }
}
