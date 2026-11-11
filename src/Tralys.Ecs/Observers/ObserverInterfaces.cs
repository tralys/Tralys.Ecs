namespace Tralys.Ecs;

/// <summary>Receives adds of component or row <typeparamref name="T"/>, enabling an <see cref="EnableableAttribute"/> one included; register it with <see cref="ObserverRegistry.OnAdd{T}"/> or <see cref="ObserverRegistry.OnRowAdd{T}"/>.</summary>
public interface IAddObserver<T>
{
    /// <summary>Called at playback for an add, with the value then. A playback reports each component's net change over the batch, so an
    /// add the batch also undid is not heard.</summary>
    void OnAdd(in ObserverContext context, Entity entity, in T value);
}

/// <summary>Receives removals of component or row <typeparamref name="T"/>, including those of an entity being destroyed and disabling an
/// <see cref="EnableableAttribute"/> one; a disabled one leaves without another.</summary>
public interface IRemoveObserver<T>
{
    /// <summary>Called at playback for a removal, with the value observers last heard. A playback reports each component's net change over
    /// the batch, so a removal the batch also undid is not heard; one re-added in the same batch is heard as a removal then an add.</summary>
    void OnRemove(in ObserverContext context, Entity entity, in T oldValue);
}

/// <summary>Receives whole-value overwrites of a <see cref="ReplaceOnlyAttribute"/> component.</summary>
public interface IReplaceObserver<T>
    where T : struct, IReplaceOnly
{
    /// <summary>Called at playback for an overwrite whose bytes differ, with both values as they were then.</summary>
    void OnReplace(in ObserverContext context, Entity entity, in T oldValue, in T newValue);
}

/// <summary>Runs before the entity's row is released, so every component is still readable.</summary>
public interface IDestroyObserver
{
    /// <summary>Called for each entity of a finalising batch, cascades included, before any is released, and for entities leaving through <see cref="World.Extract"/>; <see cref="World.CancelDestroy"/> spares it, and a throw leaves the batch pending.</summary>
    void OnDestroy(in ObserverContext context, Entity entity);
}

/// <summary>Receives edges of relation <typeparamref name="R"/> being made, broken or given a different payload.</summary>
public interface IRelationObserver<R>
{
    /// <summary>An edge was added; a symmetric relation reports its mirror edge as a second call.</summary>
    void OnRelated(in ObserverContext context, Entity source, Entity target, in R payload);

    /// <summary>An edge was removed; <paramref name="payload"/> is the payload it had.</summary>
    void OnUnrelated(in ObserverContext context, Entity source, Entity target, in R payload);

    /// <summary>An existing edge was related again with a payload whose bytes differ from the stored one.</summary>
    void OnRelationChanged(
        in ObserverContext context,
        Entity source,
        Entity target,
        in R oldPayload,
        in R newPayload
    );
}

/// <summary>Receives detaches, reattaches and restores.</summary>
public interface ILifecycleObserver
{
    /// <summary>The entity was detached, reattached or restored: <paramref name="kind"/> is <see cref="LogKind.Detached"/>,
    /// <see cref="LogKind.Reattached"/> or <see cref="LogKind.Restored"/>.</summary>
    void OnLifecycle(in ObserverContext context, Entity entity, LogKind kind);
}

/// <summary>What happened between a child and a parent.</summary>
public enum HierarchyEvent : byte
{
    /// <summary>The child was given this parent.</summary>
    ChildAdded,

    /// <summary>The child left this parent.</summary>
    ChildRemoved,

    /// <summary>The child moved to another place among this parent's children.</summary>
    ChildMoved,
}

/// <summary>Receives parent-child changes; a reparent reports <see cref="HierarchyEvent.ChildRemoved"/> from the old parent, then <see cref="HierarchyEvent.ChildAdded"/>.</summary>
public interface IHierarchyObserver
{
    /// <summary>Called at playback; <paramref name="parent"/> is the parent the event is about, the one left for <see cref="HierarchyEvent.ChildRemoved"/>.</summary>
    void OnHierarchy(in ObserverContext context, Entity child, Entity parent, HierarchyEvent kind);
}
